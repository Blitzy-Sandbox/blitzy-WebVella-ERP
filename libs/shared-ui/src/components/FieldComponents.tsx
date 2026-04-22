/**
 * FieldComponents.tsx — Central Field Type Renderer with Dynamic Dispatch
 *
 * Replaces the monolith's PcFieldBase abstract class and all 25+ PcField*
 * ViewComponents with a single FieldRenderer component that dynamically
 * dispatches to the correct field-type-specific sub-component based on the
 * FieldType enum value or string key.
 *
 * Source: WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs (options + model)
 *         WebVella.Erp.Web/Components/PcField[Star] (all 30 field type components)
 */

import { useState, useId, useCallback, useMemo, type JSX } from 'react';
import { FieldType } from '../types';
import { useFormContext } from './Form';

// ============================================================================
// SECTION 1: FieldRendererProps Interface
// Source: PcFieldBase.cs PcFieldBaseOptions (lines 19-87) + PcFieldBaseModel (89-160)
// ============================================================================

/** Props for the FieldRenderer dynamic dispatch component. */
export interface FieldRendererProps {
  /** Field name attribute — maps to PcFieldBaseOptions.Name */
  name: string;
  /** FieldType enum value or string key for sub-component dispatch */
  fieldType: FieldType | string;
  /** Current field value */
  value?: unknown;
  /** Default value when value is null/undefined — maps to PcFieldBaseModel.DefaultValue */
  defaultValue?: unknown;
  /** Change handler for edit mode — (fieldName, newValue) */
  onChange?: (name: string, value: unknown) => void;
  /** Render mode: form (editable), display (read-only), inline-edit, simple */
  mode?: 'form' | 'display' | 'inline-edit' | 'simple';
  /** Label render mode: stacked (vertical), horizontal, hidden */
  labelMode?: 'stacked' | 'horizontal' | 'hidden';
  /** Label text */
  labelText?: string;
  /** Label help text tooltip — maps to PcFieldBaseOptions.LabelHelpText */
  labelHelpText?: string;
  /** Field access level — maps to WvFieldAccess */
  access?: 'full' | 'read-only' | 'forbidden';
  /** Message when access is forbidden — default 'access denied' */
  accessDeniedMessage?: string;
  /** Required field indicator */
  required?: boolean;
  /** Validation errors for this field — maps to PcFieldBaseModel.ValidationErrors */
  validationErrors?: Array<{ key: string; value: string; message: string }>;
  /** Init errors — maps to PcFieldBaseModel.InitErrors */
  initErrors?: string[];
  /** Placeholder text — maps to PcFieldBaseModel.Placeholder */
  placeholder?: string;
  /** Field description — maps to PcFieldBaseOptions.Description */
  description?: string;
  /** CSS class — maps to PcFieldBaseOptions.Class */
  className?: string;
  /** Empty value display message — default 'no data' */
  emptyValueMessage?: string;
  /** Max character length — maps to PcFieldBaseOptions.MaxLength */
  maxLength?: number;
  /** Link URL for linkable text fields — maps to PcFieldTextOptions.Href */
  href?: string;
  /** Minimum value for numeric fields — maps to PcFieldBaseOptions.Min */
  min?: number;
  /** Maximum value for numeric fields — maps to PcFieldBaseOptions.Max */
  max?: number;
  /** Step increment for numeric fields — maps to PcFieldBaseOptions.Step */
  step?: number;
  /** Decimal digits for numeric display — default 2 */
  decimalDigits?: number;
  /** Currency code — default 'USD' */
  currencyCode?: string;
  /** AutoNumber display template — maps to PcFieldBaseOptions.Template */
  template?: string;
  /** Dropdown/list options — maps to PcFieldSelectModel.Options */
  options?: Array<{ value: string; label: string; iconClass?: string; color?: string }>;
  /** Show option icon in select — maps to PcFieldSelectOptions.ShowIcon */
  showIcon?: boolean;
  /** Grid row options for CheckboxGrid — maps to PcFieldCheckboxGridModel.Rows */
  rows?: Array<{ value: string; label: string }>;
  /** Grid column options for CheckboxGrid — maps to PcFieldCheckboxGridModel.Columns */
  gridColumns?: Array<{ value: string; label: string }>;
  /** True label text for checkbox — maps to PcFieldCheckboxOptions.TextTrue */
  textTrue?: string;
  /** False label text for checkbox — maps to PcFieldCheckboxOptions.TextFalse */
  textFalse?: string;
  /** Enable HTML toolbar for rich editor */
  enableToolbar?: boolean;
  /** API URL for file operations — maps to PcFieldBaseModel.ApiUrl */
  apiUrl?: string;
  /** Entity name context — maps to PcFieldBaseModel.EntityName */
  entityName?: string;
  /** Record ID context — maps to PcFieldBaseModel.RecordId */
  recordId?: string;
  /** Visibility flag */
  isVisible?: boolean;
  /** Locale string — maps to PcFieldBaseModel.Locale */
  locale?: string;
}

// ============================================================================
// SECTION 2: Tailwind CSS Shared Constants
// ============================================================================

const INPUT_BASE =
  'block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm transition-colors focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-gray-100 disabled:text-gray-500';
const INPUT_ERROR = 'border-red-500 focus:border-red-500 focus:ring-red-500';
const DISPLAY_BASE = 'text-sm text-gray-900';
const DISPLAY_EMPTY = 'text-sm italic text-gray-400';
const CHECKBOX_BASE = 'size-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500';
const SELECT_BASE = `${INPUT_BASE} appearance-none bg-white`;
const LABEL_BASE = 'mb-1 block text-sm font-medium text-gray-700';
const MONOSPACE = 'font-mono';

// ============================================================================
// SECTION 3: FIELD_TYPE_LABELS — Human-readable labels for all field types
// ============================================================================

/** Human-readable labels for every field type key used in the dispatch map. */
export const FIELD_TYPE_LABELS: Record<string, string> = {
  text: 'Text',
  textarea: 'Multi-line Text',
  number: 'Number',
  percent: 'Percent',
  currency: 'Currency',
  date: 'Date',
  datetime: 'Date & Time',
  time: 'Time',
  email: 'Email',
  phone: 'Phone',
  password: 'Password',
  url: 'URL',
  select: 'Dropdown',
  multiselect: 'Multi-Select',
  checkbox: 'Checkbox',
  'checkbox-list': 'Checkbox List',
  'checkbox-grid': 'Checkbox Grid',
  'radio-list': 'Radio List',
  color: 'Color',
  guid: 'Unique Identifier',
  hidden: 'Hidden',
  autonumber: 'Auto Number',
  icon: 'Icon',
  html: 'HTML',
  code: 'Code',
  file: 'File',
  image: 'Image',
  'multi-file-upload': 'Multi-File Upload',
  'data-csv': 'Data CSV',
};

// ============================================================================
// SECTION 4: Internal Types and Helpers for Sub-Components
// ============================================================================

/** Internal props passed from FieldRenderer to each sub-component. */
interface FieldSubProps {
  name: string;
  value: unknown;
  defaultValue?: unknown;
  effectiveMode: 'form' | 'display' | 'inline-edit' | 'simple';
  fieldId: string;
  hasError: boolean;
  handleChange: (newValue: unknown) => void;
  placeholder?: string;
  className?: string;
  emptyValueMessage: string;
  required?: boolean;
  disabled?: boolean;
  maxLength?: number;
  href?: string;
  min?: number;
  max?: number;
  step?: number;
  decimalDigits?: number;
  currencyCode?: string;
  template?: string;
  options?: Array<{ value: string; label: string; iconClass?: string; color?: string }>;
  showIcon?: boolean;
  rows?: Array<{ value: string; label: string }>;
  gridColumns?: Array<{ value: string; label: string }>;
  textTrue?: string;
  textFalse?: string;
  enableToolbar?: boolean;
  apiUrl?: string;
  entityName?: string;
  recordId?: string;
  locale?: string;
}

/** Resolve display value; returns empty-message string for null/undefined/empty. */
function resolveDisplay(val: unknown, empty: string): string {
  if (val === null || val === undefined || val === '') return empty;
  return String(val);
}

/** Build input class string, appending error ring when hasError is true. */
function inputCls(hasError: boolean, extra?: string): string {
  const base = hasError ? `${INPUT_BASE} ${INPUT_ERROR}` : INPUT_BASE;
  return extra ? `${base} ${extra}` : base;
}

/** Build select class string with optional error ring. */
function selectCls(hasError: boolean): string {
  return hasError ? `${SELECT_BASE} ${INPUT_ERROR}` : SELECT_BASE;
}

/** Safe string coercion — returns empty string for null/undefined. */
function safeStr(val: unknown): string {
  if (val === null || val === undefined) return '';
  return String(val);
}

/** Format a number with specified decimal digits and locale. */
function formatNumber(val: unknown, decimals: number, locale?: string): string {
  const n = Number(val);
  if (Number.isNaN(n)) return safeStr(val);
  return n.toLocaleString(locale ?? undefined, {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  });
}

// ============================================================================
// SECTION 5: Field Sub-Components (all private — not exported)
// ============================================================================

// ---------------------------------------------------------------------------
// 5.1 — TextField  (source: PcFieldText/PcFieldText.cs)
// ---------------------------------------------------------------------------
function TextField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, maxLength, href, className }: FieldSubProps) {
  const display = resolveDisplay(value, emptyValueMessage);
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{display}</span>;
    if (href) {
      return (
        <a href={href} className={`text-sm text-blue-600 underline hover:text-blue-800 ${className ?? ''}`}>
          {display}
        </a>
      );
    }
    return <span className={`${DISPLAY_BASE} ${className ?? ''}`}>{display}</span>;
  }

  return (
    <input
      id={fieldId}
      type="text"
      value={safeStr(value)}
      onChange={(e) => handleChange(e.currentTarget.value)}
      placeholder={placeholder}
      maxLength={maxLength}
      className={inputCls(hasError, className)}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.2 — TextareaField  (source: PcFieldTextarea/PcFieldTextarea.cs)
// ---------------------------------------------------------------------------
function TextareaField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, maxLength, className }: FieldSubProps) {
  const display = resolveDisplay(value, emptyValueMessage);
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{display}</span>;
    return <p className={`${DISPLAY_BASE} whitespace-pre-wrap ${className ?? ''}`}>{display}</p>;
  }

  return (
    <textarea
      id={fieldId}
      value={safeStr(value)}
      onChange={(e) => handleChange(e.currentTarget.value)}
      placeholder={placeholder}
      maxLength={maxLength}
      rows={4}
      className={inputCls(hasError, className)}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.3 — NumberField  (source: PcFieldNumber/PcFieldNumber.cs)
// ---------------------------------------------------------------------------
function NumberField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, min, max, step, decimalDigits, locale, className }: FieldSubProps) {
  const decimals = decimalDigits ?? 0;
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return <span className={`${DISPLAY_BASE} ${className ?? ''}`}>{formatNumber(value, decimals, locale)}</span>;
  }

  return (
    <input
      id={fieldId}
      type="number"
      value={safeStr(value)}
      onChange={(e) => {
        const raw = e.currentTarget.value;
        handleChange(raw === '' ? null : Number(raw));
      }}
      placeholder={placeholder}
      min={min}
      max={max}
      step={step}
      className={inputCls(hasError, className)}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.4 — PercentField  (source: PcFieldPercent/PcFieldPercent.cs)
// ---------------------------------------------------------------------------
function PercentField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, min, max, step, decimalDigits, locale, className }: FieldSubProps) {
  const decimals = decimalDigits ?? 2;
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return (
      <span className={`${DISPLAY_BASE} ${className ?? ''}`}>
        {formatNumber(value, decimals, locale)}%
      </span>
    );
  }

  return (
    <div className="relative">
      <input
        id={fieldId}
        type="number"
        value={safeStr(value)}
        onChange={(e) => {
          const raw = e.currentTarget.value;
          handleChange(raw === '' ? null : Number(raw));
        }}
        placeholder={placeholder}
        min={min}
        max={max}
        step={step}
        className={inputCls(hasError, `pe-8 ${className ?? ''}`)}
      />
      <span className="pointer-events-none absolute inset-y-0 end-0 flex items-center pe-3 text-sm text-gray-500">
        %
      </span>
    </div>
  );
}

// ---------------------------------------------------------------------------
// 5.5 — CurrencyField  (source: PcFieldCurrency/PcFieldCurrency.cs)
// ---------------------------------------------------------------------------
function CurrencyField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, min, max, step, decimalDigits, currencyCode, locale, className }: FieldSubProps) {
  const decimals = decimalDigits ?? 2;
  const code = currencyCode ?? 'USD';
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    const n = Number(value);
    const formatted = Number.isNaN(n)
      ? safeStr(value)
      : n.toLocaleString(locale ?? undefined, { style: 'currency', currency: code, minimumFractionDigits: decimals, maximumFractionDigits: decimals });
    return <span className={`${DISPLAY_BASE} ${className ?? ''}`}>{formatted}</span>;
  }

  return (
    <div className="relative">
      <span className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3 text-sm text-gray-500">
        {code}
      </span>
      <input
        id={fieldId}
        type="number"
        value={safeStr(value)}
        onChange={(e) => {
          const raw = e.currentTarget.value;
          handleChange(raw === '' ? null : Number(raw));
        }}
        placeholder={placeholder}
        min={min}
        max={max}
        step={step ?? (1 / Math.pow(10, decimals))}
        className={inputCls(hasError, `ps-14 ${className ?? ''}`)}
      />
    </div>
  );
}

// ---------------------------------------------------------------------------
// 5.6 — DateField  (source: PcFieldDate/PcFieldDate.cs)
// ---------------------------------------------------------------------------
function DateField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, locale, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    const dateStr = safeStr(value);
    const d = new Date(dateStr);
    const formatted = Number.isNaN(d.getTime()) ? dateStr : d.toLocaleDateString(locale ?? undefined);
    return <span className={`${DISPLAY_BASE} ${className ?? ''}`}>{formatted}</span>;
  }

  /* Normalize value to YYYY-MM-DD for <input type="date"> */
  const inputVal = (() => {
    const s = safeStr(value);
    if (!s) return '';
    const d = new Date(s);
    if (Number.isNaN(d.getTime())) return s;
    return d.toISOString().slice(0, 10);
  })();

  return (
    <input
      id={fieldId}
      type="date"
      value={inputVal}
      onChange={(e) => handleChange(e.currentTarget.value || null)}
      placeholder={placeholder}
      className={inputCls(hasError, className)}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.7 — DateTimeField  (source: PcFieldDateTime/PcFieldDateTime.cs)
// ---------------------------------------------------------------------------
function DateTimeField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, locale, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    const dtStr = safeStr(value);
    const d = new Date(dtStr);
    const formatted = Number.isNaN(d.getTime())
      ? dtStr
      : d.toLocaleString(locale ?? undefined);
    return <span className={`${DISPLAY_BASE} ${className ?? ''}`}>{formatted}</span>;
  }

  /* Normalize value to YYYY-MM-DDTHH:mm for <input type="datetime-local"> */
  const inputVal = (() => {
    const s = safeStr(value);
    if (!s) return '';
    const d = new Date(s);
    if (Number.isNaN(d.getTime())) return s;
    return d.toISOString().slice(0, 16);
  })();

  return (
    <input
      id={fieldId}
      type="datetime-local"
      value={inputVal}
      onChange={(e) => handleChange(e.currentTarget.value || null)}
      placeholder={placeholder}
      className={inputCls(hasError, className)}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.8 — TimeField  (source: PcFieldTime/PcFieldTime.cs)
// ---------------------------------------------------------------------------
function TimeField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return <span className={`${DISPLAY_BASE} ${className ?? ''}`}>{safeStr(value)}</span>;
  }

  return (
    <input
      id={fieldId}
      type="time"
      value={safeStr(value)}
      onChange={(e) => handleChange(e.currentTarget.value || null)}
      placeholder={placeholder}
      className={inputCls(hasError, className)}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.9 — EmailField  (source: PcFieldEmail/PcFieldEmail.cs)
// ---------------------------------------------------------------------------
function EmailField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, maxLength, className }: FieldSubProps) {
  const display = resolveDisplay(value, emptyValueMessage);
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{display}</span>;
    return (
      <a href={`mailto:${safeStr(value)}`} className={`text-sm text-blue-600 underline hover:text-blue-800 ${className ?? ''}`}>
        {display}
      </a>
    );
  }

  return (
    <input
      id={fieldId}
      type="email"
      value={safeStr(value)}
      onChange={(e) => handleChange(e.currentTarget.value)}
      placeholder={placeholder}
      maxLength={maxLength}
      className={inputCls(hasError, className)}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.10 — PhoneField  (source: PcFieldPhone/PcFieldPhone.cs)
// ---------------------------------------------------------------------------
function PhoneField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, maxLength, className }: FieldSubProps) {
  const display = resolveDisplay(value, emptyValueMessage);
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{display}</span>;
    return (
      <a href={`tel:${safeStr(value)}`} className={`text-sm text-blue-600 underline hover:text-blue-800 ${className ?? ''}`}>
        {display}
      </a>
    );
  }

  return (
    <input
      id={fieldId}
      type="tel"
      value={safeStr(value)}
      onChange={(e) => handleChange(e.currentTarget.value)}
      placeholder={placeholder}
      maxLength={maxLength}
      className={inputCls(hasError, className)}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.11 — PasswordField  (source: PcFieldPassword/PcFieldPassword.cs)
// ---------------------------------------------------------------------------
function PasswordField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, maxLength, className }: FieldSubProps) {
  const [visible, setVisible] = useState(false);
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return <span className={`${DISPLAY_BASE} ${className ?? ''}`}>{'••••••••'}</span>;
  }

  return (
    <div className="relative">
      <input
        id={fieldId}
        type={visible ? 'text' : 'password'}
        value={safeStr(value)}
        onChange={(e) => handleChange(e.currentTarget.value)}
        placeholder={placeholder}
        maxLength={maxLength}
        className={inputCls(hasError, `pe-10 ${className ?? ''}`)}
      />
      <button
        type="button"
        onClick={() => setVisible((v) => !v)}
        className="absolute inset-y-0 end-0 flex items-center pe-3 text-gray-500 hover:text-gray-700"
        aria-label={visible ? 'Hide password' : 'Show password'}
      >
        {visible ? '🙈' : '👁'}
      </button>
    </div>
  );
}

// ---------------------------------------------------------------------------
// 5.12 — UrlField  (source: PcFieldUrl/PcFieldUrl.cs)
// ---------------------------------------------------------------------------
function UrlField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, maxLength, className }: FieldSubProps) {
  const display = resolveDisplay(value, emptyValueMessage);
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{display}</span>;
    return (
      <a
        href={safeStr(value)}
        target="_blank"
        rel="noopener noreferrer"
        className={`text-sm text-blue-600 underline hover:text-blue-800 ${className ?? ''}`}
      >
        {display}
      </a>
    );
  }

  return (
    <input
      id={fieldId}
      type="url"
      value={safeStr(value)}
      onChange={(e) => handleChange(e.currentTarget.value)}
      placeholder={placeholder ?? 'https://'}
      maxLength={maxLength}
      className={inputCls(hasError, className)}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.13 — SelectField  (source: PcFieldSelect/PcFieldSelect.cs)
// ---------------------------------------------------------------------------
function SelectField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, options, showIcon, className }: FieldSubProps) {
  const opts = options ?? [];
  const selected = opts.find((o) => o.value === safeStr(value));
  const isEmpty = value === null || value === undefined || value === '';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty || !selected) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return (
      <span className={`${DISPLAY_BASE} ${className ?? ''}`}>
        {showIcon && selected.iconClass && <i className={`${selected.iconClass} me-1`} aria-hidden="true" />}
        {selected.label}
      </span>
    );
  }

  return (
    <select
      id={fieldId}
      value={safeStr(value)}
      onChange={(e) => handleChange(e.currentTarget.value)}
      className={selectCls(hasError)}
    >
      {placeholder && <option value="">{placeholder}</option>}
      {!placeholder && <option value="">{'— Select —'}</option>}
      {opts.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}

// ---------------------------------------------------------------------------
// 5.14 — MultiSelectField  (source: PcFieldMultiSelect/PcFieldMultiSelect.cs)
// ---------------------------------------------------------------------------
function MultiSelectField({ value, effectiveMode, fieldId, hasError, handleChange, emptyValueMessage, options, className }: FieldSubProps) {
  const opts = options ?? [];
  const [dropdownOpen, setDropdownOpen] = useState(false);

  /* Value is expected as string[] — normalize */
  const selectedValues: string[] = Array.isArray(value)
    ? (value as string[])
    : typeof value === 'string' && value !== ''
      ? value.split(',').map((v) => v.trim())
      : [];

  const selectedLabels = selectedValues
    .map((v) => opts.find((o) => o.value === v)?.label ?? v)
    .filter(Boolean);

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (selectedLabels.length === 0) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return (
      <div className={`flex flex-wrap gap-1 ${className ?? ''}`}>
        {selectedLabels.map((lbl, i) => (
          <span key={i} className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
            {lbl}
          </span>
        ))}
      </div>
    );
  }

  const toggle = (optValue: string) => {
    const next = selectedValues.includes(optValue)
      ? selectedValues.filter((v) => v !== optValue)
      : [...selectedValues, optValue];
    handleChange(next);
  };

  return (
    <div className="relative">
      <button
        id={fieldId}
        type="button"
        onClick={() => setDropdownOpen((o) => !o)}
        className={`${selectCls(hasError)} flex min-h-[38px] items-center justify-between text-start`}
        aria-expanded={dropdownOpen}
        aria-haspopup="listbox"
      >
        <span className="truncate">
          {selectedLabels.length > 0 ? selectedLabels.join(', ') : '— Select —'}
        </span>
        <span className="pointer-events-none ms-2 text-gray-400" aria-hidden="true">▾</span>
      </button>
      {dropdownOpen && (
        <ul
          role="listbox"
          aria-multiselectable="true"
          className="absolute z-10 mt-1 max-h-60 w-full overflow-auto rounded-md border border-gray-300 bg-white py-1 shadow-lg"
        >
          {opts.map((o) => {
            const checked = selectedValues.includes(o.value);
            return (
              <li
                key={o.value}
                role="option"
                aria-selected={checked}
                className="flex cursor-pointer items-center gap-2 px-3 py-1.5 text-sm hover:bg-gray-100"
                onClick={() => toggle(o.value)}
                onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(o.value); } }}
                tabIndex={0}
              >
                <input type="checkbox" checked={checked} readOnly className={CHECKBOX_BASE} tabIndex={-1} aria-hidden="true" />
                <span>{o.label}</span>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// 5.15 — CheckboxField  (source: PcFieldCheckbox/PcFieldCheckbox.cs)
// ---------------------------------------------------------------------------
function CheckboxField({ value, effectiveMode, fieldId, hasError, handleChange, emptyValueMessage, textTrue, textFalse, className }: FieldSubProps) {
  const checked = value === true || value === 'true' || value === 1;
  const trueText = textTrue ?? 'Yes';
  const falseText = textFalse ?? 'No';

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (value === null || value === undefined) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return <span className={`${DISPLAY_BASE} ${className ?? ''}`}>{checked ? trueText : falseText}</span>;
  }

  return (
    <label className={`inline-flex items-center gap-2 ${className ?? ''}`}>
      <input
        id={fieldId}
        type="checkbox"
        checked={checked}
        onChange={(e) => handleChange(e.currentTarget.checked)}
        className={`${CHECKBOX_BASE}${hasError ? ` ${INPUT_ERROR}` : ''}`}
      />
      <span className="text-sm text-gray-700">{checked ? trueText : falseText}</span>
    </label>
  );
}

// ---------------------------------------------------------------------------
// 5.16 — CheckboxListField  (source: PcFieldCheckboxList/PcFieldCheckboxList.cs)
// ---------------------------------------------------------------------------
function CheckboxListField({ value, effectiveMode, fieldId, hasError, handleChange, emptyValueMessage, options, className }: FieldSubProps) {
  const opts = options ?? [];
  const selectedValues: string[] = Array.isArray(value)
    ? (value as string[])
    : typeof value === 'string' && value !== ''
      ? value.split(',').map((v) => v.trim())
      : [];

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    const labels = selectedValues.map((v) => opts.find((o) => o.value === v)?.label ?? v).filter(Boolean);
    if (labels.length === 0) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return (
      <ul className={`list-inside list-disc text-sm text-gray-900 ${className ?? ''}`}>
        {labels.map((lbl, i) => <li key={i}>{lbl}</li>)}
      </ul>
    );
  }

  const toggle = (optValue: string) => {
    const next = selectedValues.includes(optValue)
      ? selectedValues.filter((v) => v !== optValue)
      : [...selectedValues, optValue];
    handleChange(next);
  };

  return (
    <fieldset className={`space-y-1 ${className ?? ''}`}>
      {opts.map((o, i) => {
        const checked = selectedValues.includes(o.value);
        const itemId = `${fieldId}-${i}`;
        return (
          <label key={o.value} htmlFor={itemId} className="flex items-center gap-2 text-sm text-gray-700">
            <input
              id={itemId}
              type="checkbox"
              checked={checked}
              onChange={() => toggle(o.value)}
              className={`${CHECKBOX_BASE}${hasError ? ` ${INPUT_ERROR}` : ''}`}
            />
            {o.label}
          </label>
        );
      })}
    </fieldset>
  );
}

// ---------------------------------------------------------------------------
// 5.17 — CheckboxGridField  (source: PcFieldCheckboxGrid/PcFieldCheckboxGrid.cs)
// ---------------------------------------------------------------------------
function CheckboxGridField({ value, effectiveMode, fieldId, hasError, handleChange, emptyValueMessage, rows: gridRows, gridColumns: gridCols, className }: FieldSubProps) {
  const rowItems = gridRows ?? [];
  const colItems = gridCols ?? [];

  /* Value structure: Record<string, string[]>  — rowValue → selected colValues */
  const gridValue: Record<string, string[]> =
    value !== null && value !== undefined && typeof value === 'object' && !Array.isArray(value)
      ? (value as Record<string, string[]>)
      : {};

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    const hasAny = Object.values(gridValue).some((arr) => arr.length > 0);
    if (!hasAny) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return (
      <table className={`text-sm ${className ?? ''}`}>
        <thead>
          <tr>
            <th className="pe-4 text-start font-medium text-gray-700" />
            {colItems.map((c) => (
              <th key={c.value} className="px-2 text-center font-medium text-gray-700">{c.label}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rowItems.map((r) => (
            <tr key={r.value}>
              <td className="pe-4 text-gray-700">{r.label}</td>
              {colItems.map((c) => (
                <td key={c.value} className="px-2 text-center">
                  {(gridValue[r.value] ?? []).includes(c.value) ? '✓' : '—'}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    );
  }

  const toggleCell = (rowVal: string, colVal: string) => {
    const currentRow = gridValue[rowVal] ?? [];
    const nextRow = currentRow.includes(colVal)
      ? currentRow.filter((v) => v !== colVal)
      : [...currentRow, colVal];
    handleChange({ ...gridValue, [rowVal]: nextRow });
  };

  return (
    <table className={`text-sm ${className ?? ''}`}>
      <thead>
        <tr>
          <th className="pe-4 text-start font-medium text-gray-700" />
          {colItems.map((c) => (
            <th key={c.value} className="px-2 text-center font-medium text-gray-700">{c.label}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rowItems.map((r) => (
          <tr key={r.value}>
            <td className="pe-4 text-gray-700">{r.label}</td>
            {colItems.map((c) => {
              const cellId = `${fieldId}-${r.value}-${c.value}`;
              const isChecked = (gridValue[r.value] ?? []).includes(c.value);
              return (
                <td key={c.value} className="px-2 text-center">
                  <input
                    id={cellId}
                    type="checkbox"
                    checked={isChecked}
                    onChange={() => toggleCell(r.value, c.value)}
                    className={`${CHECKBOX_BASE}${hasError ? ` ${INPUT_ERROR}` : ''}`}
                    aria-label={`${r.label} — ${c.label}`}
                  />
                </td>
              );
            })}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

// ---------------------------------------------------------------------------
// 5.18 — RadioListField  (source: PcFieldRadioList/PcFieldRadioList.cs)
// ---------------------------------------------------------------------------
function RadioListField({ value, effectiveMode, fieldId, hasError, handleChange, emptyValueMessage, options, className }: FieldSubProps) {
  const opts = options ?? [];
  const currentVal = safeStr(value);

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    const selected = opts.find((o) => o.value === currentVal);
    if (!selected || currentVal === '') return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return <span className={`${DISPLAY_BASE} ${className ?? ''}`}>{selected.label}</span>;
  }

  return (
    <fieldset className={`space-y-1 ${className ?? ''}`}>
      {opts.map((o, i) => {
        const itemId = `${fieldId}-${i}`;
        return (
          <label key={o.value} htmlFor={itemId} className="flex items-center gap-2 text-sm text-gray-700">
            <input
              id={itemId}
              type="radio"
              name={fieldId}
              value={o.value}
              checked={currentVal === o.value}
              onChange={() => handleChange(o.value)}
              className={`size-4 border-gray-300 text-blue-600 focus:ring-blue-500${hasError ? ` ${INPUT_ERROR}` : ''}`}
            />
            {o.label}
          </label>
        );
      })}
    </fieldset>
  );
}

// ---------------------------------------------------------------------------
// 5.19 — ColorField  (source: PcFieldColor/PcFieldColor.cs)
// ---------------------------------------------------------------------------
function ColorField({ value, effectiveMode, fieldId, hasError, handleChange, emptyValueMessage, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';
  const colorStr = safeStr(value);

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return (
      <span className={`inline-flex items-center gap-2 ${DISPLAY_BASE} ${className ?? ''}`}>
        {/* Inline style required for runtime dynamic background color — no Tailwind equivalent */}
        <span className="inline-block size-5 rounded border border-gray-300" style={{ backgroundColor: colorStr }} aria-hidden="true" />
        {colorStr}
      </span>
    );
  }

  return (
    <div className="flex items-center gap-2">
      <input
        id={fieldId}
        type="color"
        value={colorStr || '#000000'}
        onChange={(e) => handleChange(e.currentTarget.value)}
        className={`size-10 cursor-pointer rounded border border-gray-300 p-0.5${hasError ? ` ${INPUT_ERROR}` : ''}`}
      />
      <input
        type="text"
        value={colorStr}
        onChange={(e) => handleChange(e.currentTarget.value)}
        placeholder="#000000"
        maxLength={7}
        className={inputCls(hasError, `w-28 ${className ?? ''}`)}
        aria-label="Color hex value"
      />
    </div>
  );
}

// ---------------------------------------------------------------------------
// 5.20 — GuidField  (source: PcFieldGuid/PcFieldGuid.cs)
// ---------------------------------------------------------------------------
function GuidField({ value, effectiveMode, fieldId, emptyValueMessage, className }: FieldSubProps) {
  const display = resolveDisplay(value, emptyValueMessage);
  const isEmpty = value === null || value === undefined || value === '';

  /* GUIDs are always read-only — system-generated identifiers */
  if (effectiveMode === 'display' || effectiveMode === 'simple' || effectiveMode === 'form') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{display}</span>;
    return (
      <span className={`${DISPLAY_BASE} ${MONOSPACE} select-all ${className ?? ''}`}>{display}</span>
    );
  }

  /* inline-edit still shows read-only for GUID */
  return (
    <input
      id={fieldId}
      type="text"
      value={safeStr(value)}
      readOnly
      className={`${INPUT_BASE} bg-gray-50 ${MONOSPACE} ${className ?? ''}`}
      tabIndex={-1}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.21 — HiddenField  (source: PcFieldHidden/PcFieldHidden.cs)
// ---------------------------------------------------------------------------
function HiddenField({ name, value }: FieldSubProps) {
  /* Always renders as a hidden input regardless of mode */
  return <input type="hidden" name={name} value={safeStr(value)} />;
}

// ---------------------------------------------------------------------------
// 5.22 — AutoNumberField  (source: PcFieldAutonumber/PcFieldAutonumber.cs)
// ---------------------------------------------------------------------------
function AutoNumberField({ value, effectiveMode, fieldId, emptyValueMessage, template, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';

  /* Format using template if provided — replaces {0} with value */
  const formatted = (() => {
    if (isEmpty) return emptyValueMessage;
    const raw = safeStr(value);
    if (template) return template.replace('{0}', raw);
    return raw;
  })();

  /* AutoNumbers are always read-only — system-generated sequential identifiers */
  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return <span className={`${DISPLAY_BASE} ${MONOSPACE} ${className ?? ''}`}>{formatted}</span>;
  }

  /* form / inline-edit: still read-only display */
  return (
    <input
      id={fieldId}
      type="text"
      value={formatted}
      readOnly
      className={`${INPUT_BASE} bg-gray-50 ${MONOSPACE} ${className ?? ''}`}
      tabIndex={-1}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.23 — IconField  (source: PcFieldIcon/PcFieldIcon.cs)
// ---------------------------------------------------------------------------
function IconField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';
  const iconClass = safeStr(value);

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return (
      <span className={`inline-flex items-center gap-2 ${DISPLAY_BASE} ${className ?? ''}`}>
        <i className={iconClass} aria-hidden="true" />
        <span className="text-xs text-gray-500">{iconClass}</span>
      </span>
    );
  }

  return (
    <div className="flex items-center gap-2">
      {iconClass && <i className={iconClass} aria-hidden="true" />}
      <input
        id={fieldId}
        type="text"
        value={iconClass}
        onChange={(e) => handleChange(e.currentTarget.value)}
        placeholder={placeholder ?? 'e.g. fa fa-home'}
        className={inputCls(hasError, className)}
      />
    </div>
  );
}

// ---------------------------------------------------------------------------
// 5.24 — HtmlField  (source: PcFieldHtml/PcFieldHtml.cs)
// ---------------------------------------------------------------------------
function HtmlField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';
  const htmlStr = safeStr(value);

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return (
      <div
        className={`prose prose-sm max-w-none ${className ?? ''}`}
        dangerouslySetInnerHTML={{ __html: htmlStr }}
      />
    );
  }

  /* Form mode: textarea for raw HTML editing — a rich-text editor integration
     can replace this textarea in downstream consumers */
  return (
    <textarea
      id={fieldId}
      value={htmlStr}
      onChange={(e) => handleChange(e.currentTarget.value)}
      placeholder={placeholder ?? 'Enter HTML...'}
      rows={8}
      className={inputCls(hasError, `${MONOSPACE} ${className ?? ''}`)}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.25 — CodeField  (source: PcFieldCode/PcFieldCode.cs)
// ---------------------------------------------------------------------------
function CodeField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';
  const codeStr = safeStr(value);

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return (
      <pre className={`overflow-auto rounded bg-gray-50 p-3 text-sm ${className ?? ''}`}>
        <code className={MONOSPACE}>{codeStr}</code>
      </pre>
    );
  }

  return (
    <textarea
      id={fieldId}
      value={codeStr}
      onChange={(e) => handleChange(e.currentTarget.value)}
      placeholder={placeholder ?? 'Enter code...'}
      rows={10}
      className={inputCls(hasError, `${MONOSPACE} text-xs ${className ?? ''}`)}
      spellCheck={false}
    />
  );
}

// ---------------------------------------------------------------------------
// 5.26 — FileField  (source: PcFieldFile/PcFieldFile.cs)
// ---------------------------------------------------------------------------
function FileField({ value, effectiveMode, fieldId, hasError, handleChange, emptyValueMessage, apiUrl, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';
  const fileStr = safeStr(value);

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    const downloadUrl = apiUrl ? `${apiUrl}/${fileStr}` : fileStr;
    return (
      <a
        href={downloadUrl}
        target="_blank"
        rel="noopener noreferrer"
        className={`inline-flex items-center gap-1 text-sm text-blue-600 underline hover:text-blue-800 ${className ?? ''}`}
      >
        <span aria-hidden="true">📎</span>
        {fileStr}
      </a>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      {fileStr && (
        <span className="text-sm text-gray-600">Current: {fileStr}</span>
      )}
      <input
        id={fieldId}
        type="file"
        onChange={(e) => {
          const file = e.currentTarget.files?.[0] ?? null;
          handleChange(file);
        }}
        className={`block w-full text-sm text-gray-500 file:me-4 file:rounded-md file:border-0 file:bg-blue-50 file:px-4 file:py-2 file:text-sm file:font-medium file:text-blue-700 hover:file:bg-blue-100${hasError ? ` ${INPUT_ERROR}` : ''} ${className ?? ''}`}
      />
    </div>
  );
}

// ---------------------------------------------------------------------------
// 5.27 — ImageField  (source: PcFieldImage/PcFieldImage.cs)
// ---------------------------------------------------------------------------
function ImageField({ value, effectiveMode, fieldId, hasError, handleChange, emptyValueMessage, apiUrl, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';
  const imgSrc = safeStr(value);

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    const src = apiUrl ? `${apiUrl}/${imgSrc}` : imgSrc;
    return (
      <img
        src={src}
        alt=""
        loading="lazy"
        decoding="async"
        className={`max-h-48 rounded border border-gray-200 object-contain ${className ?? ''}`}
        width={192}
        height={192}
      />
    );
  }

  return (
    <div className="flex flex-col gap-2">
      {imgSrc && (
        <img
          src={apiUrl ? `${apiUrl}/${imgSrc}` : imgSrc}
          alt="Current"
          loading="lazy"
          decoding="async"
          className="max-h-32 rounded border border-gray-200 object-contain"
          width={128}
          height={128}
        />
      )}
      <input
        id={fieldId}
        type="file"
        accept="image/*"
        onChange={(e) => {
          const file = e.currentTarget.files?.[0] ?? null;
          handleChange(file);
        }}
        className={`block w-full text-sm text-gray-500 file:me-4 file:rounded-md file:border-0 file:bg-blue-50 file:px-4 file:py-2 file:text-sm file:font-medium file:text-blue-700 hover:file:bg-blue-100${hasError ? ` ${INPUT_ERROR}` : ''} ${className ?? ''}`}
      />
    </div>
  );
}

// ---------------------------------------------------------------------------
// 5.28 — MultiFileUploadField  (source: PcFieldMultiFileUpload/PcFieldMultiFileUpload.cs)
// ---------------------------------------------------------------------------
function MultiFileUploadField({ value, effectiveMode, fieldId, hasError, handleChange, emptyValueMessage, apiUrl, className }: FieldSubProps) {
  const files: string[] = Array.isArray(value)
    ? (value as string[])
    : typeof value === 'string' && value !== ''
      ? value.split(',').map((f) => f.trim())
      : [];

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (files.length === 0) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    return (
      <ul className={`space-y-1 text-sm ${className ?? ''}`}>
        {files.map((f, i) => {
          const href = apiUrl ? `${apiUrl}/${f}` : f;
          return (
            <li key={i}>
              <a href={href} target="_blank" rel="noopener noreferrer" className="text-blue-600 underline hover:text-blue-800">
                <span aria-hidden="true">📎 </span>{f}
              </a>
            </li>
          );
        })}
      </ul>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      {files.length > 0 && (
        <ul className="space-y-1 text-sm text-gray-600">
          {files.map((f, i) => <li key={i}>📎 {f}</li>)}
        </ul>
      )}
      <input
        id={fieldId}
        type="file"
        multiple
        onChange={(e) => {
          const fileList = e.currentTarget.files;
          handleChange(fileList ? Array.from(fileList) : []);
        }}
        className={`block w-full text-sm text-gray-500 file:me-4 file:rounded-md file:border-0 file:bg-blue-50 file:px-4 file:py-2 file:text-sm file:font-medium file:text-blue-700 hover:file:bg-blue-100${hasError ? ` ${INPUT_ERROR}` : ''} ${className ?? ''}`}
      />
    </div>
  );
}

// ---------------------------------------------------------------------------
// 5.29 — DataCsvField  (source: PcFieldDataCsv/PcFieldDataCsv.cs)
// ---------------------------------------------------------------------------
function DataCsvField({ value, effectiveMode, fieldId, hasError, handleChange, placeholder, emptyValueMessage, className }: FieldSubProps) {
  const isEmpty = value === null || value === undefined || value === '';
  const csvStr = safeStr(value);

  if (effectiveMode === 'display' || effectiveMode === 'simple') {
    if (isEmpty) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    /* Parse CSV into rows for tabular display */
    const rows = csvStr.split('\n').filter((r) => r.trim() !== '');
    if (rows.length === 0) return <span className={DISPLAY_EMPTY}>{emptyValueMessage}</span>;
    const cells = rows.map((r) => r.split(',').map((c) => c.trim()));
    return (
      <div className={`overflow-auto ${className ?? ''}`}>
        <table className="min-w-full text-sm">
          <tbody>
            {cells.map((row, ri) => (
              <tr key={ri} className={ri === 0 ? 'bg-gray-50 font-medium' : ''}>
                {row.map((cell, ci) => (
                  <td key={ci} className="border border-gray-200 px-2 py-1">{cell}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  }

  return (
    <textarea
      id={fieldId}
      value={csvStr}
      onChange={(e) => handleChange(e.currentTarget.value)}
      placeholder={placeholder ?? 'col1,col2,col3\nval1,val2,val3'}
      rows={6}
      className={inputCls(hasError, `${MONOSPACE} text-xs ${className ?? ''}`)}
      spellCheck={false}
    />
  );
}

// ============================================================================
// SECTION 6: FieldType-to-string Mapping for Dispatch
// ============================================================================

/**
 * Maps the numeric FieldType enum values to the string keys used in the
 * dispatch table. This bridges the two identification schemes:
 *   FieldType enum (1–21) ↔ string keys used in FIELD_TYPE_MAP.
 */
const FIELD_TYPE_ENUM_TO_KEY: Record<number, string> = {
  [FieldType.AutoNumberField]: 'autonumber',
  [FieldType.CheckboxField]: 'checkbox',
  [FieldType.CurrencyField]: 'currency',
  [FieldType.DateField]: 'date',
  [FieldType.DateTimeField]: 'datetime',
  [FieldType.EmailField]: 'email',
  [FieldType.FileField]: 'file',
  [FieldType.HtmlField]: 'html',
  [FieldType.ImageField]: 'image',
  [FieldType.MultiLineTextField]: 'textarea',
  [FieldType.MultiSelectField]: 'multiselect',
  [FieldType.NumberField]: 'number',
  [FieldType.PasswordField]: 'password',
  [FieldType.PercentField]: 'percent',
  [FieldType.PhoneField]: 'phone',
  [FieldType.GuidField]: 'guid',
  [FieldType.SelectField]: 'select',
  [FieldType.TextField]: 'text',
  [FieldType.UrlField]: 'url',
  [FieldType.RelationField]: 'text',
  [FieldType.GeographyField]: 'text',
};

/**
 * Master dispatch map: string key → sub-component renderer.
 * Both direct string keys and FieldType enum values are supported.
 */
const FIELD_TYPE_MAP: Record<string, (p: FieldSubProps) => JSX.Element | null> = {
  text: TextField,
  textarea: TextareaField,
  number: NumberField,
  percent: PercentField,
  currency: CurrencyField,
  date: DateField,
  datetime: DateTimeField,
  time: TimeField,
  email: EmailField,
  phone: PhoneField,
  password: PasswordField,
  url: UrlField,
  select: SelectField,
  multiselect: MultiSelectField,
  checkbox: CheckboxField,
  'checkbox-list': CheckboxListField,
  'checkbox-grid': CheckboxGridField,
  'radio-list': RadioListField,
  color: ColorField,
  guid: GuidField,
  hidden: HiddenField,
  autonumber: AutoNumberField,
  icon: IconField,
  html: HtmlField,
  code: CodeField,
  file: FileField,
  image: ImageField,
  'multi-file-upload': MultiFileUploadField,
  'data-csv': DataCsvField,
};

// ============================================================================
// SECTION 7: FieldRenderer — Main Dynamic Dispatcher Component
// ============================================================================

/**
 * Central field rendering component that dynamically dispatches to the
 * correct field-type-specific sub-component based on the `fieldType` prop.
 *
 * Supports display and edit modes, label configuration, access control,
 * validation error display, and form context integration.
 *
 * @example
 * ```tsx
 * <FieldRenderer
 *   name="firstName"
 *   fieldType={FieldType.TextField}
 *   value={record.firstName}
 *   onChange={(name, val) => setRecord({ ...record, [name]: val })}
 *   mode="form"
 *   labelText="First Name"
 *   required
 * />
 * ```
 */
export function FieldRenderer(props: FieldRendererProps): JSX.Element | null {
  const {
    name,
    fieldType,
    value,
    defaultValue,
    onChange,
    mode,
    labelMode: labelModeProp,
    labelText,
    labelHelpText,
    access = 'full',
    accessDeniedMessage = 'access denied',
    required = false,
    validationErrors,
    initErrors,
    placeholder,
    description,
    className,
    emptyValueMessage = 'no data',
    maxLength,
    href,
    min,
    max,
    step,
    decimalDigits,
    currencyCode,
    template,
    options,
    showIcon,
    rows,
    gridColumns,
    textTrue,
    textFalse,
    enableToolbar,
    apiUrl,
    entityName,
    recordId,
    isVisible,
    locale,
  } = props;

  // ---- Hooks (all unconditional, before any early returns) ----
  const fieldId = useId();
  const formCtx = useFormContext();
  const [isInlineEditing, setIsInlineEditing] = useState(false);

  /**
   * Resolve the sub-component for the given fieldType.
   * Supports both FieldType enum numbers and string keys.
   */
  const SubComponent = useMemo(() => {
    if (typeof fieldType === 'number') {
      const key = FIELD_TYPE_ENUM_TO_KEY[fieldType];
      return key ? FIELD_TYPE_MAP[key] : null;
    }
    return FIELD_TYPE_MAP[fieldType as string] ?? null;
  }, [fieldType]);

  /** Memoized change handler passed to sub-components. */
  const handleChange = useCallback(
    (newValue: unknown) => {
      onChange?.(name, newValue);
    },
    [name, onChange],
  );

  // ---- Visibility gate ----
  if (isVisible === false) return null;

  // ---- Access control ----
  if (access === 'forbidden') {
    return (
      <div className="mb-4">
        <span className="text-sm italic text-gray-500">{accessDeniedMessage}</span>
      </div>
    );
  }

  // ---- Resolve effective mode from context or props ----
  const resolvedMode: 'form' | 'display' | 'inline-edit' | 'simple' =
    access === 'read-only'
      ? 'display'
      : mode ?? formCtx?.fieldRenderMode ?? 'form';

  const resolvedLabelMode: 'stacked' | 'horizontal' | 'hidden' =
    labelModeProp ?? formCtx?.labelMode ?? 'stacked';

  // ---- Handle inline-edit toggle ----
  const effectiveMode: 'form' | 'display' | 'inline-edit' | 'simple' =
    resolvedMode === 'inline-edit'
      ? isInlineEditing
        ? 'form'
        : 'display'
      : resolvedMode;

  // ---- Validation errors for this field ----
  const fieldErrors = validationErrors?.filter((e) => e.key === name) ?? [];
  const hasError = fieldErrors.length > 0;

  // ---- Resolve effective value ----
  const effectiveValue = value !== undefined && value !== null ? value : (defaultValue ?? null);

  // ---- Build sub-component props ----
  const subProps: FieldSubProps = {
    name,
    value: effectiveValue,
    defaultValue,
    effectiveMode,
    fieldId,
    hasError,
    handleChange,
    placeholder,
    className,
    emptyValueMessage,
    required,
    disabled: access === 'read-only',
    maxLength,
    href,
    min,
    max,
    step,
    decimalDigits,
    currencyCode,
    template,
    options,
    showIcon,
    rows,
    gridColumns,
    textTrue,
    textFalse,
    enableToolbar,
    apiUrl,
    entityName,
    recordId,
    locale,
  };

  // ---- Render sub-component (or fallback) ----
  const renderedField = SubComponent
    ? <SubComponent {...subProps} />
    : <span className="text-sm text-red-500">Unknown field type: {String(fieldType)}</span>;

  // ---- Init errors block ----
  const initErrorBlock = initErrors && initErrors.length > 0 ? (
    <div className="mb-2">
      {initErrors.map((err, i) => (
        <p key={i} className="text-xs text-orange-600">{err}</p>
      ))}
    </div>
  ) : null;

  // ---- Validation error block ----
  const errorBlock = hasError ? (
    <div role="alert" aria-live="polite">
      {fieldErrors.map((err, i) => (
        <p key={i} className="mt-1 text-xs text-red-600">{err.message}</p>
      ))}
    </div>
  ) : null;

  // ---- Inline-edit wrapper ----
  const fieldWithInlineEdit = resolvedMode === 'inline-edit' ? (
    <div
      className="group relative cursor-pointer rounded px-1 py-0.5 hover:bg-gray-50"
      onClick={() => !isInlineEditing && setIsInlineEditing(true)}
      onKeyDown={(e) => { if (e.key === 'Enter' && !isInlineEditing) setIsInlineEditing(true); }}
      tabIndex={isInlineEditing ? -1 : 0}
      role="button"
      aria-label={`Edit ${labelText ?? name}`}
    >
      {renderedField}
      {isInlineEditing && (
        <div className="mt-1 flex gap-1">
          <button
            type="button"
            className="rounded bg-blue-600 px-2 py-0.5 text-xs text-white hover:bg-blue-700"
            onClick={(e) => { e.stopPropagation(); setIsInlineEditing(false); }}
          >
            Save
          </button>
          <button
            type="button"
            className="rounded bg-gray-200 px-2 py-0.5 text-xs text-gray-700 hover:bg-gray-300"
            onClick={(e) => { e.stopPropagation(); setIsInlineEditing(false); }}
          >
            Cancel
          </button>
        </div>
      )}
    </div>
  ) : renderedField;

  // ---- Required indicator ----
  const requiredMark = required ? (
    <span className="text-red-500" aria-hidden="true"> *</span>
  ) : null;

  // ---- Label rendering based on labelMode ----
  if (resolvedLabelMode === 'hidden') {
    return (
      <div className="mb-4">
        <label htmlFor={fieldId} className="sr-only">{labelText ?? name}</label>
        {initErrorBlock}
        {fieldWithInlineEdit}
        {errorBlock}
      </div>
    );
  }

  if (resolvedLabelMode === 'horizontal') {
    return (
      <div className="mb-4 grid grid-cols-[200px_1fr] items-start gap-x-4">
        <label htmlFor={fieldId} className="pt-2 text-end text-sm font-medium text-gray-700">
          {labelText ?? name}{requiredMark}
        </label>
        <div>
          {initErrorBlock}
          {fieldWithInlineEdit}
          {description && <p className="mt-1 text-xs text-gray-500">{description}</p>}
          {errorBlock}
        </div>
      </div>
    );
  }

  /* Default: stacked label layout */
  return (
    <div className="mb-4">
      <label htmlFor={fieldId} className={LABEL_BASE}>
        {labelText ?? name}{requiredMark}
      </label>
      {labelHelpText && (
        <span className="mb-1 block text-xs text-gray-500">{labelHelpText}</span>
      )}
      {initErrorBlock}
      {fieldWithInlineEdit}
      {description && <p className="mt-1 text-xs text-gray-500">{description}</p>}
      {errorBlock}
    </div>
  );
}
