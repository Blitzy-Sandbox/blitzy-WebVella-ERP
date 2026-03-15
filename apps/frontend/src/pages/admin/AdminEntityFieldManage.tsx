/**
 * AdminEntityFieldManage — Edit an existing entity field.
 *
 * Route: /admin/entities/:entityId/fields/:fieldId/manage
 *
 * Replaces the monolith's manage-field.cshtml + manage-field.cshtml.cs.
 * Loads the entity and its fields, finds the target field by ID,
 * pre-populates a form with the current field properties, provides
 * type-specific configuration sections for all 20 field types, and
 * submits updates via the useUpdateField() mutation.
 */

import React, { useState, useEffect, useCallback, useMemo } from 'react';
import { useParams, useNavigate, Link } from 'react-router';

import { useEntity, useEntityFields, useUpdateField } from '../../hooks/useEntities';
import { useRoles } from '../../hooks/useUsers';
import type {
  Entity,
  AnyField,
  Field,
  FieldPermissions,
  SelectOption,
} from '../../types/entity';
import { FieldType, GeographyFieldFormat } from '../../types/entity';
import type { ErpRole } from '../../types/user';
import DynamicForm from '../../components/forms/DynamicForm';
import FormSection from '../../components/forms/FormSection';
import FormRow from '../../components/forms/FormRow';

/* -----------------------------------------------------------------------
 * Constants
 * --------------------------------------------------------------------- */

/** Common ISO 4217 currencies for the CurrencyField code dropdown. */
const CURRENCY_OPTIONS: Array<{ code: string; symbol: string; name: string }> = [
  { code: 'USD', symbol: '$', name: 'US Dollar' },
  { code: 'EUR', symbol: '€', name: 'Euro' },
  { code: 'GBP', symbol: '£', name: 'British Pound' },
  { code: 'BGN', symbol: 'лв', name: 'Bulgarian Lev' },
  { code: 'JPY', symbol: '¥', name: 'Japanese Yen' },
  { code: 'CNY', symbol: '¥', name: 'Chinese Yuan' },
  { code: 'CAD', symbol: 'CA$', name: 'Canadian Dollar' },
  { code: 'AUD', symbol: 'A$', name: 'Australian Dollar' },
  { code: 'CHF', symbol: 'CHF', name: 'Swiss Franc' },
  { code: 'SEK', symbol: 'kr', name: 'Swedish Krona' },
  { code: 'INR', symbol: '₹', name: 'Indian Rupee' },
  { code: 'BRL', symbol: 'R$', name: 'Brazilian Real' },
  { code: 'RUB', symbol: '₽', name: 'Russian Ruble' },
  { code: 'KRW', symbol: '₩', name: 'South Korean Won' },
  { code: 'TRY', symbol: '₺', name: 'Turkish Lira' },
  { code: 'PLN', symbol: 'zł', name: 'Polish Zloty' },
  { code: 'RON', symbol: 'lei', name: 'Romanian Leu' },
  { code: 'HUF', symbol: 'Ft', name: 'Hungarian Forint' },
  { code: 'CZK', symbol: 'Kč', name: 'Czech Koruna' },
  { code: 'DKK', symbol: 'kr', name: 'Danish Krone' },
  { code: 'NOK', symbol: 'kr', name: 'Norwegian Krone' },
  { code: 'HRK', symbol: 'kn', name: 'Croatian Kuna' },
  { code: 'MXN', symbol: 'MX$', name: 'Mexican Peso' },
  { code: 'ZAR', symbol: 'R', name: 'South African Rand' },
  { code: 'NZD', symbol: 'NZ$', name: 'New Zealand Dollar' },
];

/* -----------------------------------------------------------------------
 * Helpers
 * --------------------------------------------------------------------- */

/** Map FieldType enum to human-readable display names. */
function getFieldTypeName(ft: FieldType): string {
  switch (ft) {
    case FieldType.AutoNumberField:
      return 'Auto Number';
    case FieldType.CheckboxField:
      return 'Checkbox';
    case FieldType.CurrencyField:
      return 'Currency';
    case FieldType.DateField:
      return 'Date';
    case FieldType.DateTimeField:
      return 'DateTime';
    case FieldType.EmailField:
      return 'Email';
    case FieldType.FileField:
      return 'File';
    case FieldType.HtmlField:
      return 'Html';
    case FieldType.ImageField:
      return 'Image';
    case FieldType.MultiLineTextField:
      return 'Multi-Line Text';
    case FieldType.MultiSelectField:
      return 'Multi Select';
    case FieldType.NumberField:
      return 'Number';
    case FieldType.PasswordField:
      return 'Password';
    case FieldType.PercentField:
      return 'Percent';
    case FieldType.PhoneField:
      return 'Phone';
    case FieldType.GuidField:
      return 'Guid';
    case FieldType.SelectField:
      return 'Select';
    case FieldType.TextField:
      return 'Text';
    case FieldType.UrlField:
      return 'Url';
    case FieldType.GeographyField:
      return 'Geography';
    default:
      return 'Unknown';
  }
}

/**
 * Parse newline-separated select option text into SelectOption[].
 *
 * Each line follows the format used by the monolith:
 *   value
 *   value,label
 *   value,label,iconClass
 *   value,label,iconClass,color
 */
function parseSelectOptions(text: string): SelectOption[] {
  if (!text.trim()) return [];
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => {
      const parts = line.split(',').map((p) => p.trim());
      return {
        value: parts[0] ?? '',
        label: parts[1] ?? parts[0] ?? '',
        iconClass: parts[2] ?? '',
        color: parts[3] ?? '',
      };
    });
}

/** Format SelectOption[] back to the newline-separated text representation. */
function formatSelectOptions(options: SelectOption[]): string {
  if (!options || options.length === 0) return '';
  return options
    .map((opt) => {
      const parts = [opt.value];
      if (opt.label && opt.label !== opt.value) parts.push(opt.label);
      else if (opt.iconClass || opt.color) parts.push(opt.label || opt.value);
      if (opt.iconClass) parts.push(opt.iconClass);
      else if (opt.color) parts.push('');
      if (opt.color) parts.push(opt.color);
      return parts.join(',');
    })
    .join('\n');
}

/* -----------------------------------------------------------------------
 * Shared Tailwind class names
 * --------------------------------------------------------------------- */

const inputClasses =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm ' +
  'text-gray-900 shadow-sm placeholder:text-gray-400 ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-500 ' +
  'disabled:cursor-not-allowed disabled:bg-gray-100 disabled:text-gray-500';

const readonlyClasses =
  'block w-full rounded-md border border-gray-200 bg-gray-100 px-3 py-2 text-sm text-gray-600';

const labelClasses = 'block text-sm font-medium text-gray-700 mb-1';

const checkboxClasses =
  'h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500';

const selectClasses =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm ' +
  'text-gray-900 shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500';

const textareaClasses =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm ' +
  'text-gray-900 shadow-sm placeholder:text-gray-400 ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500';

const errorTextClasses = 'mt-1 text-xs text-red-600';

/* -----------------------------------------------------------------------
 * Component
 * --------------------------------------------------------------------- */

function AdminEntityFieldManage(): React.JSX.Element {
  /* --- Route params --------------------------------------------------- */
  const { entityId = '', fieldId = '' } = useParams();
  const navigate = useNavigate();

  /* --- Data fetching -------------------------------------------------- */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityError,
    error: entityFetchError,
  } = useEntity(entityId);

  const { data: rolesData, isLoading: rolesLoading } = useRoles();
  const { data: apiFields } = useEntityFields(entityId);
  const updateFieldMutation = useUpdateField();

  /* --- Derived values ------------------------------------------------- */
  const roles: ErpRole[] = useMemo(
    () => (rolesData as { object?: ErpRole[] } | undefined)?.object ?? [],
    [rolesData],
  );

  const currentField: AnyField | null = useMemo(() => {
    if (!fieldId) return null;
    // Prefer fields from the dedicated API endpoint; fall back to entity.fields
    const allFields = (apiFields && apiFields.length > 0)
      ? apiFields
      : (entity?.fields ?? []);
    const found = (allFields as Field[]).find((f: Field) => f.id === fieldId);
    return found ? (found as AnyField) : null;
  }, [apiFields, entity, fieldId]);

  /* --- Form state ----------------------------------------------------- */
  const [fieldData, setFieldData] = useState<AnyField | null>(null);
  const [selectOptionsText, setSelectOptionsText] = useState('');
  const [multiSelectDefaultText, setMultiSelectDefaultText] = useState('');
  const [validationErrors, setValidationErrors] = useState<Record<string, string>>({});
  const [showSuccess, setShowSuccess] = useState(false);
  const [isInitialized, setIsInitialized] = useState(false);

  /* --- Pre-populate effect -------------------------------------------- */
  useEffect(() => {
    if (currentField && !isInitialized) {
      setFieldData({ ...currentField });
      setIsInitialized(true);

      /* Pre-populate select/multiselect option text areas */
      const cfRecord = currentField as unknown as Record<string, unknown>;
      if ('options' in currentField && Array.isArray(cfRecord.options)) {
        setSelectOptionsText(
          formatSelectOptions(cfRecord.options as SelectOption[]),
        );
      }
      if (
        currentField.fieldType === FieldType.MultiSelectField &&
        'defaultValue' in currentField
      ) {
        const dv = cfRecord.defaultValue;
        setMultiSelectDefaultText(Array.isArray(dv) ? (dv as string[]).join('\n') : '');
      }
    }
  }, [currentField, isInitialized]);

  /* --- Field property updater ----------------------------------------- */
  const updateProp = useCallback((key: string, value: unknown): void => {
    setFieldData((prev) => (prev ? { ...prev, [key]: value } : prev));
    setValidationErrors((prev) => {
      if (!(key in prev)) return prev;
      const next = { ...prev };
      delete next[key];
      return next;
    });
  }, []);

  /* --- Permission toggle handler -------------------------------------- */
  const togglePermission = useCallback(
    (roleId: string, permType: 'canRead' | 'canUpdate'): void => {
      setFieldData((prev) => {
        if (!prev) return prev;
        const perms: FieldPermissions = {
          canRead: [...(prev.permissions?.canRead ?? [])],
          canUpdate: [...(prev.permissions?.canUpdate ?? [])],
        };
        const arr = perms[permType];
        const idx = arr.indexOf(roleId);
        if (idx >= 0) {
          arr.splice(idx, 1);
        } else {
          arr.push(roleId);
        }
        return { ...prev, permissions: perms };
      });
    },
    [],
  );

  /* --- Enable-security toggle ----------------------------------------- */
  const toggleEnableSecurity = useCallback((): void => {
    setFieldData((prev) => (prev ? { ...prev, enableSecurity: !prev.enableSecurity } : prev));
  }, []);

  /* --- Form submission ------------------------------------------------ */
  const handleSubmit = useCallback(
    (e?: React.FormEvent): void => {
      if (e) e.preventDefault();
      if (!fieldData || !entityId || !fieldId) return;

      const errors: Record<string, string> = {};

      /* Common validation */
      if (!fieldData.label?.trim()) {
        errors.label = 'Label is required';
      }

      /* Build updated field, handling select/multiselect options */
      const updatedField: Record<string, unknown> = { ...fieldData };

      if (
        fieldData.fieldType === FieldType.SelectField ||
        fieldData.fieldType === FieldType.MultiSelectField
      ) {
        const parsedOptions = parseSelectOptions(selectOptionsText);
        updatedField.options = parsedOptions;

        if (fieldData.fieldType === FieldType.MultiSelectField) {
          const defaults = multiSelectDefaultText
            .split('\n')
            .map((s) => s.trim())
            .filter(Boolean);
          const optionValues = new Set(parsedOptions.map((o) => o.value));
          const invalid = defaults.filter((d) => !optionValues.has(d));
          if (invalid.length > 0) {
            errors.defaultValue = `Invalid default value(s): ${invalid.join(', ')}`;
          } else {
            updatedField.defaultValue = defaults;
          }
        } else {
          const dv = updatedField.defaultValue as string;
          if (dv && parsedOptions.length > 0 && !parsedOptions.some((o) => o.value === dv)) {
            errors.defaultValue = 'Default value must match one of the options';
          }
        }
      }

      if (Object.keys(errors).length > 0) {
        setValidationErrors(errors);
        return;
      }

      setValidationErrors({});
      updateFieldMutation.mutate(
        { entityId, fieldId, field: updatedField as unknown as AnyField },
        {
          onSuccess: () => {
            setShowSuccess(true);
            setTimeout(() => navigate(`/admin/entities/${entityId}/fields`), 1500);
          },
        },
      );
    },
    [
      fieldData,
      entityId,
      fieldId,
      selectOptionsText,
      multiSelectDefaultText,
      updateFieldMutation,
      navigate,
    ],
  );

  /* --- Type-specific configuration section content -------------------- */
  const typeSpecificContent = useMemo((): React.ReactNode => {
    if (!fieldData) return null;

    switch (fieldData.fieldType) {
      /* ---- Auto Number ---- */
      case FieldType.AutoNumberField:
        return (
          <>
            <FormRow>
              <div>
                <label className={labelClasses}>Default Value</label>
                <input
                  type="text"
                  className={readonlyClasses}
                  value={fieldData.defaultValue ?? ''}
                  readOnly
                  aria-label="Default value (read-only)"
                />
                <p className="mt-1 text-xs text-gray-500">Auto-generated — cannot be changed.</p>
              </div>
              <div>
                <label className={labelClasses}>Starting Number</label>
                <input
                  type="text"
                  className={readonlyClasses}
                  value={fieldData.startingNumber ?? ''}
                  readOnly
                  aria-label="Starting number (read-only)"
                />
              </div>
            </FormRow>
            <FormRow visibleColumns={1}>
              <div>
                <label className={labelClasses} htmlFor="ff-displayFormat">
                  Display Format
                </label>
                <input
                  id="ff-displayFormat"
                  type="text"
                  className={inputClasses}
                  value={fieldData.displayFormat ?? ''}
                  placeholder='e.g. {0:00000}'
                  onChange={(e) => updateProp('displayFormat', e.target.value)}
                />
                <p className="mt-1 text-xs text-gray-500">
                  C# string format pattern. Example: {'{0:00000}'} produces 00001, 00002…
                </p>
              </div>
            </FormRow>
          </>
        );

      /* ---- Checkbox ---- */
      case FieldType.CheckboxField:
        return (
          <FormRow visibleColumns={1}>
            <div className="flex items-center gap-2">
              <input
                id="ff-defaultValue"
                type="checkbox"
                className={checkboxClasses}
                checked={fieldData.defaultValue === true}
                onChange={(e) => updateProp('defaultValue', e.target.checked)}
              />
              <label htmlFor="ff-defaultValue" className="text-sm text-gray-700">
                Default Checked
              </label>
            </div>
          </FormRow>
        );

      /* ---- Currency ---- */
      case FieldType.CurrencyField:
        return (
          <>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-currencyDefaultValue">
                  Default Value
                </label>
                <input
                  id="ff-currencyDefaultValue"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.defaultValue ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'defaultValue',
                      e.target.value === '' ? null : parseFloat(e.target.value),
                    )
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-currencyCode">
                  Currency Code
                </label>
                <select
                  id="ff-currencyCode"
                  className={selectClasses}
                  value={fieldData.currency?.code ?? 'USD'}
                  onChange={(e) => {
                    const sel = CURRENCY_OPTIONS.find((c) => c.code === e.target.value);
                    if (sel) {
                      updateProp('currency', {
                        ...(fieldData.currency ?? {}),
                        code: sel.code,
                        symbol: sel.symbol,
                        symbolNative: sel.symbol,
                        name: sel.name,
                        namePlural: sel.name + 's',
                      });
                    }
                  }}
                >
                  {CURRENCY_OPTIONS.map((c) => (
                    <option key={c.code} value={c.code}>
                      {c.code} — {c.name} ({c.symbol})
                    </option>
                  ))}
                </select>
              </div>
            </FormRow>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-currencyMin">
                  Min Value
                </label>
                <input
                  id="ff-currencyMin"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.minValue ?? ''}
                  onChange={(e) =>
                    updateProp('minValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-currencyMax">
                  Max Value
                </label>
                <input
                  id="ff-currencyMax"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.maxValue ?? ''}
                  onChange={(e) =>
                    updateProp('maxValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
            </FormRow>
          </>
        );

      /* ---- Date ---- */
      case FieldType.DateField:
        return (
          <>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-dateDefault">
                  Default Value
                </label>
                <input
                  id="ff-dateDefault"
                  type="date"
                  className={inputClasses}
                  value={fieldData.defaultValue ?? ''}
                  onChange={(e) => updateProp('defaultValue', e.target.value || null)}
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-dateFormat">
                  Format
                </label>
                <input
                  id="ff-dateFormat"
                  type="text"
                  className={inputClasses}
                  value={fieldData.format ?? ''}
                  placeholder="e.g. yyyy-MM-dd"
                  onChange={(e) => updateProp('format', e.target.value)}
                />
              </div>
            </FormRow>
            <FormRow visibleColumns={1}>
              <div className="flex items-center gap-2">
                <input
                  id="ff-dateUseCurrent"
                  type="checkbox"
                  className={checkboxClasses}
                  checked={fieldData.useCurrentTimeAsDefaultValue === true}
                  onChange={(e) => updateProp('useCurrentTimeAsDefaultValue', e.target.checked)}
                />
                <label htmlFor="ff-dateUseCurrent" className="text-sm text-gray-700">
                  Use current date as default value
                </label>
              </div>
            </FormRow>
          </>
        );

      /* ---- DateTime ---- */
      case FieldType.DateTimeField:
        return (
          <>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-datetimeDefault">
                  Default Value
                </label>
                <input
                  id="ff-datetimeDefault"
                  type="datetime-local"
                  className={inputClasses}
                  value={fieldData.defaultValue ?? ''}
                  onChange={(e) => updateProp('defaultValue', e.target.value || null)}
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-datetimeFormat">
                  Format
                </label>
                <input
                  id="ff-datetimeFormat"
                  type="text"
                  className={inputClasses}
                  value={fieldData.format ?? ''}
                  placeholder="e.g. yyyy-MM-dd HH:mm"
                  onChange={(e) => updateProp('format', e.target.value)}
                />
              </div>
            </FormRow>
            <FormRow visibleColumns={1}>
              <div className="flex items-center gap-2">
                <input
                  id="ff-dtUseCurrent"
                  type="checkbox"
                  className={checkboxClasses}
                  checked={fieldData.useCurrentTimeAsDefaultValue === true}
                  onChange={(e) => updateProp('useCurrentTimeAsDefaultValue', e.target.checked)}
                />
                <label htmlFor="ff-dtUseCurrent" className="text-sm text-gray-700">
                  Use current date/time as default value
                </label>
              </div>
            </FormRow>
          </>
        );

      /* ---- Email ---- */
      case FieldType.EmailField:
        return (
          <FormRow>
            <div>
              <label className={labelClasses} htmlFor="ff-emailDefault">
                Default Value
              </label>
              <input
                id="ff-emailDefault"
                type="email"
                className={inputClasses}
                value={fieldData.defaultValue ?? ''}
                onChange={(e) => updateProp('defaultValue', e.target.value)}
              />
            </div>
            <div>
              <label className={labelClasses} htmlFor="ff-emailMaxLen">
                Max Length
              </label>
              <input
                id="ff-emailMaxLen"
                type="number"
                min={0}
                className={inputClasses}
                value={fieldData.maxLength ?? ''}
                onChange={(e) =>
                  updateProp('maxLength', e.target.value === '' ? null : parseInt(e.target.value, 10))
                }
              />
            </div>
          </FormRow>
        );

      /* ---- File ---- */
      case FieldType.FileField:
        return (
          <FormRow visibleColumns={1}>
            <div>
              <label className={labelClasses} htmlFor="ff-fileDefault">
                Default Value
              </label>
              <input
                id="ff-fileDefault"
                type="text"
                className={inputClasses}
                value={fieldData.defaultValue ?? ''}
                placeholder="Default file path or URL"
                onChange={(e) => updateProp('defaultValue', e.target.value)}
              />
            </div>
          </FormRow>
        );

      /* ---- Html ---- */
      case FieldType.HtmlField:
        return (
          <FormRow visibleColumns={1}>
            <div>
              <label className={labelClasses} htmlFor="ff-htmlDefault">
                Default Value
              </label>
              <textarea
                id="ff-htmlDefault"
                className={textareaClasses}
                rows={4}
                value={fieldData.defaultValue ?? ''}
                onChange={(e) => updateProp('defaultValue', e.target.value)}
              />
            </div>
          </FormRow>
        );

      /* ---- Image ---- */
      case FieldType.ImageField:
        return (
          <FormRow visibleColumns={1}>
            <div>
              <label className={labelClasses} htmlFor="ff-imageDefault">
                Default Value
              </label>
              <input
                id="ff-imageDefault"
                type="text"
                className={inputClasses}
                value={fieldData.defaultValue ?? ''}
                placeholder="Default image path or URL"
                onChange={(e) => updateProp('defaultValue', e.target.value)}
              />
            </div>
          </FormRow>
        );

      /* ---- Multi-Line Text ---- */
      case FieldType.MultiLineTextField:
        return (
          <>
            <FormRow visibleColumns={1}>
              <div>
                <label className={labelClasses} htmlFor="ff-mltDefault">
                  Default Value
                </label>
                <textarea
                  id="ff-mltDefault"
                  className={textareaClasses}
                  rows={4}
                  value={fieldData.defaultValue ?? ''}
                  onChange={(e) => updateProp('defaultValue', e.target.value)}
                />
              </div>
            </FormRow>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-mltMaxLen">
                  Max Length
                </label>
                <input
                  id="ff-mltMaxLen"
                  type="number"
                  min={0}
                  className={inputClasses}
                  value={fieldData.maxLength ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'maxLength',
                      e.target.value === '' ? null : parseInt(e.target.value, 10),
                    )
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-mltVisibleLines">
                  Visible Line Number
                </label>
                <input
                  id="ff-mltVisibleLines"
                  type="number"
                  min={1}
                  className={inputClasses}
                  value={fieldData.visibleLineNumber ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'visibleLineNumber',
                      e.target.value === '' ? null : parseInt(e.target.value, 10),
                    )
                  }
                />
              </div>
            </FormRow>
          </>
        );

      /* ---- Multi Select ---- */
      case FieldType.MultiSelectField:
        return (
          <>
            <FormRow visibleColumns={1}>
              <div>
                <label className={labelClasses} htmlFor="ff-msOptions">
                  Options <span className="text-gray-400">(one per line: value or value,label or value,label,iconClass or value,label,iconClass,color)</span>
                </label>
                <textarea
                  id="ff-msOptions"
                  className={textareaClasses}
                  rows={6}
                  value={selectOptionsText}
                  onChange={(e) => setSelectOptionsText(e.target.value)}
                />
              </div>
            </FormRow>
            <FormRow visibleColumns={1}>
              <div>
                <label className={labelClasses} htmlFor="ff-msDefault">
                  Default Value(s) <span className="text-gray-400">(one per line, must match option values)</span>
                </label>
                <textarea
                  id="ff-msDefault"
                  className={textareaClasses}
                  rows={3}
                  value={multiSelectDefaultText}
                  onChange={(e) => setMultiSelectDefaultText(e.target.value)}
                />
                {validationErrors.defaultValue && (
                  <p className={errorTextClasses}>{validationErrors.defaultValue}</p>
                )}
              </div>
            </FormRow>
          </>
        );

      /* ---- Number ---- */
      case FieldType.NumberField:
        return (
          <>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-numDefault">
                  Default Value
                </label>
                <input
                  id="ff-numDefault"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.defaultValue ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'defaultValue',
                      e.target.value === '' ? null : parseFloat(e.target.value),
                    )
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-numDecimals">
                  Decimal Places
                </label>
                <input
                  id="ff-numDecimals"
                  type="number"
                  min={0}
                  max={10}
                  className={inputClasses}
                  value={fieldData.decimalPlaces ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'decimalPlaces',
                      e.target.value === '' ? null : parseInt(e.target.value, 10),
                    )
                  }
                />
              </div>
            </FormRow>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-numMin">
                  Min Value
                </label>
                <input
                  id="ff-numMin"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.minValue ?? ''}
                  onChange={(e) =>
                    updateProp('minValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-numMax">
                  Max Value
                </label>
                <input
                  id="ff-numMax"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.maxValue ?? ''}
                  onChange={(e) =>
                    updateProp('maxValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
            </FormRow>
          </>
        );

      /* ---- Password ---- */
      case FieldType.PasswordField:
        return (
          <>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-pwdMaxLen">
                  Max Length
                </label>
                <input
                  id="ff-pwdMaxLen"
                  type="number"
                  min={0}
                  className={inputClasses}
                  value={fieldData.maxLength ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'maxLength',
                      e.target.value === '' ? null : parseInt(e.target.value, 10),
                    )
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-pwdMinLen">
                  Min Length
                </label>
                <input
                  id="ff-pwdMinLen"
                  type="number"
                  min={0}
                  className={inputClasses}
                  value={fieldData.minLength ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'minLength',
                      e.target.value === '' ? null : parseInt(e.target.value, 10),
                    )
                  }
                />
              </div>
            </FormRow>
            <FormRow visibleColumns={1}>
              <div className="flex items-center gap-2">
                <input
                  id="ff-pwdEncrypted"
                  type="checkbox"
                  className={checkboxClasses}
                  checked={fieldData.encrypted === true}
                  onChange={(e) => updateProp('encrypted', e.target.checked)}
                />
                <label htmlFor="ff-pwdEncrypted" className="text-sm text-gray-700">
                  Encrypted
                </label>
              </div>
            </FormRow>
          </>
        );

      /* ---- Percent ---- */
      case FieldType.PercentField:
        return (
          <>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-pctDefault">
                  Default Value
                </label>
                <input
                  id="ff-pctDefault"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.defaultValue ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'defaultValue',
                      e.target.value === '' ? null : parseFloat(e.target.value),
                    )
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-pctDecimals">
                  Decimal Places
                </label>
                <input
                  id="ff-pctDecimals"
                  type="number"
                  min={0}
                  max={10}
                  className={inputClasses}
                  value={fieldData.decimalPlaces ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'decimalPlaces',
                      e.target.value === '' ? null : parseInt(e.target.value, 10),
                    )
                  }
                />
              </div>
            </FormRow>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-pctMin">
                  Min Value
                </label>
                <input
                  id="ff-pctMin"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.minValue ?? ''}
                  onChange={(e) =>
                    updateProp('minValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-pctMax">
                  Max Value
                </label>
                <input
                  id="ff-pctMax"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.maxValue ?? ''}
                  onChange={(e) =>
                    updateProp('maxValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
            </FormRow>
          </>
        );

      /* ---- Phone ---- */
      case FieldType.PhoneField:
        return (
          <FormRow>
            <div>
              <label className={labelClasses} htmlFor="ff-phoneDefault">
                Default Value
              </label>
              <input
                id="ff-phoneDefault"
                type="tel"
                className={inputClasses}
                value={fieldData.defaultValue ?? ''}
                onChange={(e) => updateProp('defaultValue', e.target.value)}
              />
            </div>
            <div>
              <label className={labelClasses} htmlFor="ff-phoneMaxLen">
                Max Length
              </label>
              <input
                id="ff-phoneMaxLen"
                type="number"
                min={0}
                className={inputClasses}
                value={fieldData.maxLength ?? ''}
                onChange={(e) =>
                  updateProp(
                    'maxLength',
                    e.target.value === '' ? null : parseInt(e.target.value, 10),
                  )
                }
              />
            </div>
          </FormRow>
        );

      /* ---- Guid ---- */
      case FieldType.GuidField:
        return (
          <>
            <FormRow visibleColumns={1}>
              <div>
                <label className={labelClasses} htmlFor="ff-guidDefault">
                  Default Value
                </label>
                <input
                  id="ff-guidDefault"
                  type="text"
                  className={inputClasses}
                  value={fieldData.defaultValue ?? ''}
                  placeholder="e.g. 00000000-0000-0000-0000-000000000000"
                  onChange={(e) => updateProp('defaultValue', e.target.value || null)}
                />
              </div>
            </FormRow>
            <FormRow visibleColumns={1}>
              <div className="flex items-center gap-2">
                <input
                  id="ff-guidGenerate"
                  type="checkbox"
                  className={checkboxClasses}
                  checked={fieldData.generateNewId === true}
                  onChange={(e) => updateProp('generateNewId', e.target.checked)}
                />
                <label htmlFor="ff-guidGenerate" className="text-sm text-gray-700">
                  Generate new ID as default value
                </label>
              </div>
            </FormRow>
          </>
        );

      /* ---- Select ---- */
      case FieldType.SelectField:
        return (
          <>
            <FormRow visibleColumns={1}>
              <div>
                <label className={labelClasses} htmlFor="ff-selOptions">
                  Options <span className="text-gray-400">(one per line: value or value,label or value,label,iconClass or value,label,iconClass,color)</span>
                </label>
                <textarea
                  id="ff-selOptions"
                  className={textareaClasses}
                  rows={6}
                  value={selectOptionsText}
                  onChange={(e) => setSelectOptionsText(e.target.value)}
                />
              </div>
            </FormRow>
            <FormRow visibleColumns={1}>
              <div>
                <label className={labelClasses} htmlFor="ff-selDefault">
                  Default Value
                </label>
                <input
                  id="ff-selDefault"
                  type="text"
                  className={inputClasses}
                  value={fieldData.defaultValue ?? ''}
                  placeholder="Must match one of the option values above"
                  onChange={(e) => updateProp('defaultValue', e.target.value)}
                />
                {validationErrors.defaultValue && (
                  <p className={errorTextClasses}>{validationErrors.defaultValue}</p>
                )}
              </div>
            </FormRow>
          </>
        );

      /* ---- Text ---- */
      case FieldType.TextField:
        return (
          <FormRow>
            <div>
              <label className={labelClasses} htmlFor="ff-textDefault">
                Default Value
              </label>
              <input
                id="ff-textDefault"
                type="text"
                className={inputClasses}
                value={fieldData.defaultValue ?? ''}
                onChange={(e) => updateProp('defaultValue', e.target.value)}
              />
            </div>
            <div>
              <label className={labelClasses} htmlFor="ff-textMaxLen">
                Max Length
              </label>
              <input
                id="ff-textMaxLen"
                type="number"
                min={0}
                className={inputClasses}
                value={fieldData.maxLength ?? ''}
                onChange={(e) =>
                  updateProp(
                    'maxLength',
                    e.target.value === '' ? null : parseInt(e.target.value, 10),
                  )
                }
              />
            </div>
          </FormRow>
        );

      /* ---- Url ---- */
      case FieldType.UrlField:
        return (
          <>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-urlDefault">
                  Default Value
                </label>
                <input
                  id="ff-urlDefault"
                  type="url"
                  className={inputClasses}
                  value={fieldData.defaultValue ?? ''}
                  onChange={(e) => updateProp('defaultValue', e.target.value)}
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-urlMaxLen">
                  Max Length
                </label>
                <input
                  id="ff-urlMaxLen"
                  type="number"
                  min={0}
                  className={inputClasses}
                  value={fieldData.maxLength ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'maxLength',
                      e.target.value === '' ? null : parseInt(e.target.value, 10),
                    )
                  }
                />
              </div>
            </FormRow>
            <FormRow visibleColumns={1}>
              <div className="flex items-center gap-2">
                <input
                  id="ff-urlNewWindow"
                  type="checkbox"
                  className={checkboxClasses}
                  checked={fieldData.openTargetInNewWindow === true}
                  onChange={(e) => updateProp('openTargetInNewWindow', e.target.checked)}
                />
                <label htmlFor="ff-urlNewWindow" className="text-sm text-gray-700">
                  Open target in new window
                </label>
              </div>
            </FormRow>
          </>
        );

      /* ---- Geography ---- */
      case FieldType.GeographyField:
        return (
          <>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-geoFormat">
                  Format
                </label>
                <select
                  id="ff-geoFormat"
                  className={selectClasses}
                  value={fieldData.format ?? GeographyFieldFormat.GeoJSON}
                  onChange={(e) =>
                    updateProp('format', parseInt(e.target.value, 10) as GeographyFieldFormat)
                  }
                >
                  <option value={GeographyFieldFormat.GeoJSON}>GeoJSON</option>
                  <option value={GeographyFieldFormat.Text}>Text (WKT)</option>
                </select>
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-geoSrid">
                  SRID
                </label>
                <input
                  id="ff-geoSrid"
                  type="number"
                  className={inputClasses}
                  value={fieldData.srid ?? 4326}
                  onChange={(e) =>
                    updateProp('srid', parseInt(e.target.value, 10) || 4326)
                  }
                />
                <p className="mt-1 text-xs text-gray-500">Default: 4326 (WGS 84)</p>
              </div>
            </FormRow>
            <FormRow>
              <div>
                <label className={labelClasses} htmlFor="ff-geoMaxLen">
                  Max Length
                </label>
                <input
                  id="ff-geoMaxLen"
                  type="number"
                  min={0}
                  className={inputClasses}
                  value={fieldData.maxLength ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'maxLength',
                      e.target.value === '' ? null : parseInt(e.target.value, 10),
                    )
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-geoVisibleLines">
                  Visible Line Number
                </label>
                <input
                  id="ff-geoVisibleLines"
                  type="number"
                  min={1}
                  className={inputClasses}
                  value={fieldData.visibleLineNumber ?? ''}
                  onChange={(e) =>
                    updateProp(
                      'visibleLineNumber',
                      e.target.value === '' ? null : parseInt(e.target.value, 10),
                    )
                  }
                />
              </div>
            </FormRow>
          </>
        );

      /* ---- Relation / Unknown ---- */
      default:
        return (
          <p className="text-sm text-gray-500 italic">
            No additional configuration is available for this field type.
          </p>
        );
    }
  }, [fieldData, selectOptionsText, multiSelectDefaultText, validationErrors, updateProp]);

  /* -------------------------------------------------------------------
   * Loading / Error / Not-found guards
   * ----------------------------------------------------------------- */

  if (entityLoading || rolesLoading) {
    return (
      <div className="flex items-center justify-center py-16" role="status">
        <svg
          className="animate-spin h-8 w-8 text-blue-600"
          viewBox="0 0 24 24"
          fill="none"
          aria-hidden="true"
        >
          <circle
            className="opacity-25"
            cx="12"
            cy="12"
            r="10"
            stroke="currentColor"
            strokeWidth="4"
          />
          <path
            className="opacity-75"
            fill="currentColor"
            d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
          />
        </svg>
        <span className="sr-only">Loading field data…</span>
      </div>
    );
  }

  if (entityError) {
    return (
      <div className="rounded-md border border-red-200 bg-red-50 p-6 text-center" role="alert">
        <h2 className="text-lg font-semibold text-red-800">Error Loading Entity</h2>
        <p className="mt-2 text-sm text-red-600">
          {entityFetchError instanceof Error ? entityFetchError.message : 'An unexpected error occurred.'}
        </p>
        <Link
          to="/admin/entities"
          className="mt-4 inline-block text-sm font-medium text-blue-600 hover:text-blue-800"
        >
          ← Back to Entities
        </Link>
      </div>
    );
  }

  if (!entity || !fieldData) {
    return (
      <div className="rounded-md border border-yellow-200 bg-yellow-50 p-6 text-center" role="alert">
        <h2 className="text-lg font-semibold text-yellow-800">Field Not Found</h2>
        <p className="mt-2 text-sm text-yellow-600">
          The requested field could not be found in this entity.
        </p>
        <Link
          to={`/admin/entities/${entityId}/fields`}
          className="mt-4 inline-block text-sm font-medium text-blue-600 hover:text-blue-800"
        >
          ← Back to Fields
        </Link>
      </div>
    );
  }

  /* -------------------------------------------------------------------
   * Render
   * ----------------------------------------------------------------- */
  return (
    <div className="mx-auto max-w-5xl px-4 py-6 sm:px-6 lg:px-8">
      {showSuccess && (
        <div className="mb-4 rounded-md bg-green-50 p-4" role="status" aria-live="polite">
          <p className="text-sm font-medium text-green-800" data-testid="success-notification">Field saved successfully. Redirecting…</p>
        </div>
      )}
      {/* Breadcrumb */}
      <nav className="mb-4 text-sm text-gray-500" aria-label="Breadcrumb">
        <ol className="flex items-center gap-1">
          <li>
            <Link to="/admin/entities" className="hover:text-blue-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 rounded">
              Entities
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li>
            <Link
              to={`/admin/entities/${entityId}/fields`}
              className="hover:text-blue-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 rounded"
            >
              {entity.name}
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li>
            <Link
              to={`/admin/entities/${entityId}/fields`}
              className="hover:text-blue-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 rounded"
            >
              Fields
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li className="text-gray-900 font-medium" aria-current="page">
            Edit: {fieldData.name}
          </li>
        </ol>
      </nav>

      {/* Page header */}
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900">
          Edit Field: {fieldData.name}
        </h1>
        <p className="mt-1 text-sm text-gray-500">
          {getFieldTypeName(fieldData.fieldType)} field on entity{' '}
          <span className="font-medium text-gray-700">{entity.label}</span>
        </p>
      </div>

      {/* Mutation error alert */}
      {updateFieldMutation.isError && (
        <div className="mb-4 rounded-md border border-red-200 bg-red-50 p-4" role="alert">
          <p className="text-sm text-red-700">
            {updateFieldMutation.error instanceof Error
              ? updateFieldMutation.error.message
              : 'Failed to update field. Please try again.'}
          </p>
        </div>
      )}

      <DynamicForm onSubmit={handleSubmit}>
        {/* ======================== General Section ======================== */}
        <FormSection title="General" isCard isCollapsible>
          <FormRow>
            <div>
              <label className={labelClasses}>Name</label>
              <input
                type="text"
                className={readonlyClasses}
                value={fieldData.name ?? ''}
                readOnly
                aria-label="Field name (read-only)"
              />
              <p className="mt-1 text-xs text-gray-500">Field name cannot be changed after creation.</p>
            </div>
            <div>
              <label className={labelClasses} htmlFor="ff-label">
                Label <span className="text-red-500">*</span>
              </label>
              <input
                id="ff-label"
                name="label"
                type="text"
                className={inputClasses}
                value={fieldData.label ?? ''}
                onChange={(e) => updateProp('label', e.target.value)}
                required
              />
              {validationErrors.label && (
                <p className={errorTextClasses}>{validationErrors.label}</p>
              )}
            </div>
          </FormRow>

          <FormRow>
            <div>
              <label className={labelClasses}>Field Id</label>
              <input
                type="text"
                className={readonlyClasses}
                value={fieldData.id ?? ''}
                readOnly
                aria-label="Field ID (read-only)"
              />
            </div>
            <div>
              <label className={labelClasses}>Field Type</label>
              <input
                type="text"
                className={readonlyClasses}
                value={getFieldTypeName(fieldData.fieldType)}
                readOnly
                aria-label="Field type (read-only)"
              />
            </div>
          </FormRow>

          <FormRow>
            <div className="flex items-center gap-2 pt-6">
              <input
                id="ff-required"
                type="checkbox"
                className={checkboxClasses}
                checked={fieldData.required === true}
                onChange={(e) => updateProp('required', e.target.checked)}
              />
              <label htmlFor="ff-required" className="text-sm text-gray-700">
                Required
              </label>
            </div>
            <div className="flex items-center gap-2 pt-6">
              <input
                id="ff-unique"
                type="checkbox"
                className={checkboxClasses}
                checked={fieldData.unique === true}
                onChange={(e) => updateProp('unique', e.target.checked)}
              />
              <label htmlFor="ff-unique" className="text-sm text-gray-700">
                Unique
              </label>
            </div>
          </FormRow>

          <FormRow>
            <div className="flex items-center gap-2 pt-2">
              <input
                id="ff-searchable"
                type="checkbox"
                className={checkboxClasses}
                checked={fieldData.searchable === true}
                onChange={(e) => updateProp('searchable', e.target.checked)}
              />
              <label htmlFor="ff-searchable" className="text-sm text-gray-700">
                Searchable
              </label>
            </div>
            <div className="flex items-center gap-2 pt-2">
              <input
                id="ff-auditable"
                type="checkbox"
                className={checkboxClasses}
                checked={fieldData.auditable === true}
                onChange={(e) => updateProp('auditable', e.target.checked)}
              />
              <label htmlFor="ff-auditable" className="text-sm text-gray-700">
                Auditable
              </label>
            </div>
          </FormRow>

          <FormRow visibleColumns={1}>
            <div>
              <label className={labelClasses} htmlFor="ff-description">
                Description
              </label>
              <textarea
                id="ff-description"
                className={textareaClasses}
                rows={2}
                value={fieldData.description ?? ''}
                onChange={(e) => updateProp('description', e.target.value)}
              />
            </div>
          </FormRow>

          <FormRow>
            <div>
              <label className={labelClasses} htmlFor="ff-helpText">
                Help Text
              </label>
              <input
                id="ff-helpText"
                type="text"
                className={inputClasses}
                value={fieldData.helpText ?? ''}
                onChange={(e) => updateProp('helpText', e.target.value)}
              />
            </div>
            <div>
              <label className={labelClasses} htmlFor="ff-placeholder">
                Placeholder Text
              </label>
              <input
                id="ff-placeholder"
                type="text"
                className={inputClasses}
                value={fieldData.placeholderText ?? ''}
                onChange={(e) => updateProp('placeholderText', e.target.value)}
              />
            </div>
          </FormRow>

          <FormRow visibleColumns={1}>
            <div className="flex items-center gap-2">
              <input
                id="ff-system"
                type="checkbox"
                className={checkboxClasses}
                checked={fieldData.system === true}
                disabled
                aria-label="System field (read-only)"
              />
              <label htmlFor="ff-system" className="text-sm text-gray-500">
                System field {fieldData.system ? '(Yes)' : '(No)'}
              </label>
            </div>
          </FormRow>
        </FormSection>

        {/* ================= Type-Specific Section ================== */}
        <FormSection
          title={`${getFieldTypeName(fieldData.fieldType)} Field Properties`}
          isCard
          isCollapsible
        >
          {typeSpecificContent}
        </FormSection>

        {/* ================== API Security Section ================== */}
        <FormSection title="API Security" isCard isCollapsible>
          <FormRow visibleColumns={1}>
            <div className="flex items-center gap-2">
              <input
                id="ff-enableSecurity"
                type="checkbox"
                className={checkboxClasses}
                checked={fieldData.enableSecurity === true}
                onChange={toggleEnableSecurity}
              />
              <label htmlFor="ff-enableSecurity" className="text-sm text-gray-700">
                Enable field-level security
              </label>
            </div>
          </FormRow>

          {fieldData.enableSecurity && roles.length > 0 && (
            <div className="mt-4 overflow-x-auto">
              <table className="min-w-full divide-y divide-gray-200 border border-gray-200 rounded-md">
                <thead className="bg-gray-50">
                  <tr>
                    <th
                      scope="col"
                      className="px-4 py-3 text-start text-xs font-medium uppercase tracking-wider text-gray-500"
                    >
                      Role
                    </th>
                    <th
                      scope="col"
                      className="px-4 py-3 text-center text-xs font-medium uppercase tracking-wider text-gray-500"
                    >
                      Read
                    </th>
                    <th
                      scope="col"
                      className="px-4 py-3 text-center text-xs font-medium uppercase tracking-wider text-gray-500"
                    >
                      Update
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100 bg-white">
                  {roles.map((role: ErpRole) => {
                    const canRead = fieldData.permissions?.canRead?.includes(role.id) ?? false;
                    const canUpdate = fieldData.permissions?.canUpdate?.includes(role.id) ?? false;
                    return (
                      
      <tr key={role.id}>
                        <td className="whitespace-nowrap px-4 py-3 text-sm text-gray-900">
                          {role.name}
                        </td>
                        <td className="px-4 py-3 text-center">
                          <input
                            type="checkbox"
                            className={checkboxClasses}
                            checked={canRead}
                            onChange={() => togglePermission(role.id, 'canRead')}
                            aria-label={`Read permission for ${role.name}`}
                          />
                        </td>
                        <td className="px-4 py-3 text-center">
                          <input
                            type="checkbox"
                            className={checkboxClasses}
                            checked={canUpdate}
                            onChange={() => togglePermission(role.id, 'canUpdate')}
                            aria-label={`Update permission for ${role.name}`}
                          />
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}

          {fieldData.enableSecurity && roles.length === 0 && (
            <p className="mt-2 text-sm text-gray-500 italic">No roles available.</p>
          )}
        </FormSection>

        {/* =================== Action Buttons =================== */}
        <div className="mt-6 flex items-center gap-3">
          <button
            type="submit"
            disabled={updateFieldMutation.isPending}
            className={
              'inline-flex items-center rounded-md px-4 py-2 text-sm font-semibold text-white shadow-sm ' +
              'bg-blue-600 hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 ' +
              'disabled:opacity-50 disabled:cursor-not-allowed'
            }
          >
            {updateFieldMutation.isPending ? 'Saving…' : 'Save Field'}
          </button>
          <Link
            to={`/admin/entities/${entityId}/fields`}
            className={
              'inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-semibold ' +
              'text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500'
            }
          >
            Cancel
          </Link>
        </div>
      </DynamicForm>
    </div>
  );
}

export default AdminEntityFieldManage;
