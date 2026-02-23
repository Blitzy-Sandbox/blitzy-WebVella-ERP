/**
 * STUB — CheckboxField component.
 *
 * Minimal type-correct stub created to satisfy FieldRenderer.tsx dynamic
 * imports during compilation. This file will be replaced with a full
 * implementation by its assigned agent.
 */

import React from 'react';
import type { BaseFieldProps } from './FieldRenderer';

function CheckboxField(props: BaseFieldProps): React.JSX.Element {
  const displayValue =
    props.value !== null && props.value !== undefined
      ? String(props.value)
      : props.emptyValueMessage ?? 'no data';

  if (props.mode === 'display') {
    return (
      <span className="text-sm text-gray-900">{displayValue}</span>
    );
  }

  return (
    <input
      type="text"
      id={props.fieldId ?? `field-${props.name}`}
      name={props.name}
      value={
        props.value !== null && props.value !== undefined
          ? String(props.value)
          : ''
      }
      onChange={(e) => props.onChange?.(e.target.value)}
      placeholder={props.placeholder}
      disabled={props.disabled}
      required={props.required}
      className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-gray-100 disabled:text-gray-500"
      aria-invalid={Boolean(props.error)}
      aria-describedby={props.error ? `${props.name}-error` : undefined}
    />
  );
}

export default CheckboxField;
