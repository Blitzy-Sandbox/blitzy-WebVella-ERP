/**
 * CheckboxField — Single Checkbox (Boolean Toggle)
 *
 * React replacement for the monolith's PcFieldCheckbox ViewComponent.
 * Renders a single checkbox for boolean toggle values with configurable
 * true/false label text, edit mode with native checkbox input, and
 * display mode with visual check/cross indicators.
 *
 * @module CheckboxField
 */

import React, { useState, useCallback } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

/* ------------------------------------------------------------------ */
/*  Props Interface                                                    */
/* ------------------------------------------------------------------ */

/**
 * Props for the CheckboxField component when used directly (not via
 * FieldRenderer). Extends BaseFieldProps with boolean-specific value
 * and onChange typing, plus textTrue / textFalse label configuration.
 *
 * When CheckboxField is consumed through FieldRenderer's lazy-loaded
 * map, it receives BaseFieldProps and internally narrows the types.
 */
export interface CheckboxFieldProps
  extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Current boolean value; null is treated as false */
  value: boolean | null;
  /** Callback invoked when the user toggles the checkbox */
  onChange?: (value: boolean) => void;
  /** Label text displayed when checked (default: "selected") */
  textTrue?: string;
  /** Label text displayed when unchecked (default: "not selected") */
  textFalse?: string;
}

/* ------------------------------------------------------------------ */
/*  Helpers                                                            */
/* ------------------------------------------------------------------ */

/**
 * Coerce an unknown value to a boolean.
 * Accepts: boolean `true`/`false`, string "true"/"false", or null/undefined → false.
 */
function coerceToBoolean(raw: unknown): boolean {
  if (typeof raw === 'boolean') return raw;
  if (typeof raw === 'string') return raw.toLowerCase() === 'true';
  return false;
}

/* ------------------------------------------------------------------ */
/*  Component                                                          */
/* ------------------------------------------------------------------ */

/**
 * CheckboxField renders a single boolean toggle.
 *
 * **Edit mode** — native `<input type="checkbox">` with Tailwind
 * styling and a textTrue/textFalse label that reflects the current
 * checked state.
 *
 * **Display mode** — visual indicator (✓ green / ✗ gray) with the
 * corresponding textTrue/textFalse label.
 *
 * The component respects `access` (readonly / forbidden), `disabled`,
 * `required`, and `error` props inherited from BaseFieldProps.
 *
 * The function signature uses BaseFieldProps for compatibility with the
 * FieldRenderer FIELD_COMPONENT_MAP. Checkbox-specific props (textTrue,
 * textFalse) are extracted with safe defaults.
 */
function CheckboxField(props: BaseFieldProps): React.JSX.Element {
  const {
    /* Identity */
    name,

    /* Value — typed as unknown in BaseFieldProps, coerced below */
    value: rawValue,
    onChange: rawOnChange,

    /* Label — consumed by FieldRenderer wrapper, destructured here
       for completeness so rest-spread does not leak them */
    label,
    labelMode,

    /* Mode and access */
    mode = 'edit',
    access = 'full',

    /* Validation */
    required = false,
    disabled = false,
    error,

    /* Appearance */
    className,
    placeholder,
    description,
    isVisible,

    /* Messages */
    accessDeniedMessage = 'access denied',
    emptyValueMessage = 'no data',
    locale,

    /* fieldId for input/label association */
    fieldId,
  } = props;

  /* ---- Extract checkbox-specific props with defaults --------------- */

  const checkboxProps = props as unknown as Partial<
    Pick<CheckboxFieldProps, 'textTrue' | 'textFalse'>
  >;
  const textTrue: string = checkboxProps.textTrue ?? 'selected';
  const textFalse: string = checkboxProps.textFalse ?? 'not selected';

  /* ---- Derived state ------------------------------------------------ */

  /** Coerce unknown value to boolean for consistent logic */
  const isChecked: boolean = coerceToBoolean(rawValue);

  /** Determine if the field should be effectively readonly */
  const isReadonly: boolean = access === 'readonly' || disabled;

  /** Unique id for the input/label association */
  const inputId: string = fieldId ?? `field-${name}`;

  /** Type-narrow onChange to boolean callback */
  const onChange = rawOnChange as ((value: boolean) => void) | undefined;

  /* ---- Internal state for uncontrolled fallback in edit mode -------- */
  const [internalChecked, setInternalChecked] = useState<boolean>(isChecked);

  /**
   * When value is provided (not null/undefined), use the prop-derived
   * boolean. Otherwise fall back to internal state for uncontrolled usage.
   */
  const effectiveChecked: boolean =
    rawValue !== null && rawValue !== undefined
      ? coerceToBoolean(rawValue)
      : internalChecked;

  /** Text label that matches the effective checked state */
  const stateLabel: string = effectiveChecked ? textTrue : textFalse;

  /* ---- Handlers ----------------------------------------------------- */

  /**
   * Memoised change handler. Toggles the boolean value and propagates
   * the new state to the parent via onChange.
   */
  const handleChange = useCallback(() => {
    if (isReadonly) return;

    const nextValue = !effectiveChecked;
    setInternalChecked(nextValue);
    onChange?.(nextValue);
  }, [effectiveChecked, isReadonly, onChange]);

  /* ---- Access denied guard ----------------------------------------- */

  if (access === 'forbidden') {
    return (
      <div
        className={`flex items-center gap-2 text-sm text-gray-400 italic${
          className ? ` ${className}` : ''
        }`}
        role="status"
        aria-label={accessDeniedMessage}
      >
        {/* Lock icon */}
        <svg
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 20 20"
          fill="currentColor"
          className="h-4 w-4 shrink-0"
          aria-hidden="true"
        >
          <path
            fillRule="evenodd"
            d="M10 1a4.5 4.5 0 0 0-4.5 4.5V9H5a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-6a2 2 0 0 0-2-2h-.5V5.5A4.5 4.5 0 0 0 10 1Zm3 8V5.5a3 3 0 1 0-6 0V9h6Z"
            clipRule="evenodd"
          />
        </svg>
        <span>{accessDeniedMessage}</span>
      </div>
    );
  }

  /* ---- Display mode ------------------------------------------------ */

  if (mode === 'display') {
    return (
      <div
        className={`inline-flex items-center gap-2 text-sm${
          className ? ` ${className}` : ''
        }`}
        data-field-name={name}
        data-field-mode="display"
      >
        {effectiveChecked ? (
          /* Checkmark indicator — green */
          <span
            className="inline-flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-green-100 text-green-600"
            aria-hidden="true"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-3.5 w-3.5"
            >
              <path
                fillRule="evenodd"
                d="M16.704 4.153a.75.75 0 0 1 .143 1.052l-8 10.5a.75.75 0 0 1-1.127.075l-4.5-4.5a.75.75 0 0 1 1.06-1.06l3.894 3.893 7.48-9.817a.75.75 0 0 1 1.05-.143Z"
                clipRule="evenodd"
              />
            </svg>
          </span>
        ) : (
          /* Cross indicator — gray */
          <span
            className="inline-flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-gray-100 text-gray-400"
            aria-hidden="true"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-3.5 w-3.5"
            >
              <path d="M6.28 5.22a.75.75 0 0 0-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 1 0 1.06 1.06L10 11.06l3.72 3.72a.75.75 0 1 0 1.06-1.06L11.06 10l3.72-3.72a.75.75 0 0 0-1.06-1.06L10 8.94 6.28 5.22Z" />
            </svg>
          </span>
        )}
        <span className={effectiveChecked ? 'text-gray-900' : 'text-gray-500'}>
          {stateLabel}
        </span>
      </div>
    );
  }

  /* ---- Edit mode ---------------------------------------------------- */

  return (
    <div
      className={`relative flex items-start${
        className ? ` ${className}` : ''
      }`}
      data-field-name={name}
      data-field-mode="edit"
    >
      <div className="flex h-6 items-center">
        <input
          id={inputId}
          name={name}
          type="checkbox"
          checked={effectiveChecked}
          onChange={handleChange}
          disabled={isReadonly}
          required={required}
          className={[
            'h-4 w-4 rounded border-gray-300 text-blue-600',
            'focus:ring-blue-500 focus:ring-2 focus:ring-offset-0',
            isReadonly
              ? 'cursor-not-allowed bg-gray-100 opacity-60'
              : 'cursor-pointer',
            error ? 'border-red-500' : '',
          ]
            .filter(Boolean)
            .join(' ')}
          aria-invalid={error ? true : undefined}
          aria-describedby={
            error
              ? `${inputId}-error`
              : description
                ? `${inputId}-description`
                : undefined
          }
          aria-required={required || undefined}
        />
      </div>

      <div className="ms-3 text-sm leading-6">
        <label
          htmlFor={inputId}
          className={[
            'font-medium',
            isReadonly
              ? 'text-gray-400 cursor-not-allowed'
              : 'text-gray-900 cursor-pointer',
          ].join(' ')}
        >
          {stateLabel}
        </label>

        {/* Description text below the label */}
        {description && (
          <p id={`${inputId}-description`} className="text-gray-500">
            {description}
          </p>
        )}

        {/* Error message */}
        {error && (
          <p
            id={`${inputId}-error`}
            className="mt-1 text-sm text-red-600"
            role="alert"
          >
            {error}
          </p>
        )}
      </div>
    </div>
  );
}

export default CheckboxField;
