/**
 * AdminEntityFieldCreate — Create a new entity field.
 *
 * Route: /admin/entities/:entityId/fields/create
 *
 * Two-step flow combining the monolith's create-field-select.cshtml (field type
 * chooser cards) and create-field.cshtml (creation form). Supports all 20 field
 * types with type-specific configuration sections, common field properties, and
 * field-level security permission grid.
 */

import { useState, useCallback, useMemo } from 'react';
import { useParams, useNavigate, Link } from 'react-router';

import { useEntity, useCreateField } from '../../hooks/useEntities';
import { useRoles } from '../../hooks/useUsers';
import type {
  Entity,
  AnyField,
  SelectOption,
  CurrencyType,
  FieldPermissions,
} from '../../types/entity';
import { FieldType, GeographyFieldFormat } from '../../types/entity';
import type { ErpRole } from '../../types/user';

/* =========================================================================
 * Constants
 * ========================================================================= */

/** Descriptor for field type selection cards shown in Step 1. */
interface FieldTypeCardDef {
  type: FieldType;
  label: string;
  description: string;
  icon: string;
}

/**
 * All selectable field types presented as cards. Relation (20) is excluded
 * because the monolith renders it as read-only / separate workflow.
 */
const FIELD_TYPE_CARDS: FieldTypeCardDef[] = [
  { type: FieldType.AutoNumberField, label: 'Auto Number', description: 'Auto-incrementing number field', icon: '🔢' },
  { type: FieldType.CheckboxField, label: 'Checkbox', description: 'Boolean true/false toggle', icon: '☑️' },
  { type: FieldType.CurrencyField, label: 'Currency', description: 'Monetary value with currency code', icon: '💲' },
  { type: FieldType.DateField, label: 'Date', description: 'Date only value', icon: '📅' },
  { type: FieldType.DateTimeField, label: 'DateTime', description: 'Date and time value', icon: '📆' },
  { type: FieldType.EmailField, label: 'Email', description: 'Email address field', icon: '✉️' },
  { type: FieldType.FileField, label: 'File', description: 'File upload reference', icon: '📎' },
  { type: FieldType.GeographyField, label: 'Geography', description: 'GeoJSON or text spatial data', icon: '🌍' },
  { type: FieldType.HtmlField, label: 'Html', description: 'Rich HTML content', icon: '🖋️' },
  { type: FieldType.ImageField, label: 'Image', description: 'Image file reference', icon: '🖼️' },
  { type: FieldType.MultiLineTextField, label: 'Multi-Line Text', description: 'Multi-line plain text area', icon: '📝' },
  { type: FieldType.MultiSelectField, label: 'Multi Select', description: 'Multiple choice from a list', icon: '☰' },
  { type: FieldType.NumberField, label: 'Number', description: 'Numeric value with precision', icon: '#️⃣' },
  { type: FieldType.PasswordField, label: 'Password', description: 'Encrypted secret field', icon: '🔑' },
  { type: FieldType.PercentField, label: 'Percent', description: 'Percentage numeric value', icon: '%' },
  { type: FieldType.PhoneField, label: 'Phone', description: 'Phone number field', icon: '📞' },
  { type: FieldType.GuidField, label: 'Guid', description: 'Globally unique identifier', icon: '🆔' },
  { type: FieldType.SelectField, label: 'Select', description: 'Single choice from a list', icon: '📋' },
  { type: FieldType.TextField, label: 'Text', description: 'Single-line text field', icon: '🔤' },
  { type: FieldType.UrlField, label: 'Url', description: 'Web URL with optional new-tab', icon: '🔗' },
];

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

/* =========================================================================
 * Helpers
 * ========================================================================= */

/** Map FieldType enum to human-readable display names. */
function getFieldTypeName(ft: FieldType): string {
  const card = FIELD_TYPE_CARDS.find((c) => c.type === ft);
  return card?.label ?? 'Unknown';
}

/**
 * Parse newline-separated select option text into SelectOption[].
 *
 * Each line follows the monolith format:
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

/** Generate a new v4 UUID string for field ID. */
function generateId(): string {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) {
    return crypto.randomUUID();
  }
  /* Fallback for older environments */
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

/**
 * Build the default field data for a selected field type.
 * Mirrors the monolith's OnGet defaults in create-field.cshtml.cs.
 */
function buildDefaultFieldData(ft: FieldType): Record<string, unknown> {
  const base: Record<string, unknown> = {
    id: generateId(),
    name: '',
    label: '',
    fieldType: ft,
    required: false,
    unique: false,
    searchable: false,
    auditable: false,
    system: false,
    description: '',
    helpText: '',
    placeholderText: '',
    enableSecurity: false,
    permissions: { canRead: [], canUpdate: [] } as FieldPermissions,
  };

  switch (ft) {
    case FieldType.AutoNumberField:
      return { ...base, defaultValue: null, displayFormat: '{0}', startingNumber: 1 };
    case FieldType.CheckboxField:
      return { ...base, defaultValue: false };
    case FieldType.CurrencyField:
      return {
        ...base,
        defaultValue: null,
        minValue: null,
        maxValue: null,
        currency: {
          code: 'USD',
          symbol: '$',
          symbolNative: '$',
          name: 'US Dollar',
          namePlural: 'US Dollars',
          decimalDigits: 2,
          rounding: 0,
        } as CurrencyType,
      };
    case FieldType.DateField:
      return { ...base, defaultValue: null, format: 'yyyy-MMM-dd', useCurrentTimeAsDefaultValue: false };
    case FieldType.DateTimeField:
      return { ...base, defaultValue: null, format: 'yyyy-MMM-dd HH:mm', useCurrentTimeAsDefaultValue: false };
    case FieldType.EmailField:
      return { ...base, defaultValue: '', maxLength: null };
    case FieldType.FileField:
      return { ...base, defaultValue: '' };
    case FieldType.GeographyField:
      return { ...base, defaultValue: '', maxLength: null, visibleLineNumber: null, format: GeographyFieldFormat.GeoJSON, srid: 4326 };
    case FieldType.HtmlField:
      return { ...base, defaultValue: '' };
    case FieldType.ImageField:
      return { ...base, defaultValue: '' };
    case FieldType.MultiLineTextField:
      return { ...base, defaultValue: '', maxLength: null, visibleLineNumber: null };
    case FieldType.MultiSelectField:
      return { ...base, defaultValue: [], options: [] };
    case FieldType.NumberField:
      return { ...base, defaultValue: null, minValue: null, maxValue: null, decimalPlaces: 2 };
    case FieldType.PasswordField:
      return { ...base, maxLength: null, minLength: null, encrypted: true };
    case FieldType.PercentField:
      return { ...base, defaultValue: null, minValue: null, maxValue: null, decimalPlaces: 2 };
    case FieldType.PhoneField:
      return { ...base, defaultValue: '', maxLength: null, format: '' };
    case FieldType.GuidField:
      return { ...base, defaultValue: null, generateNewId: false };
    case FieldType.SelectField:
      return { ...base, defaultValue: '', options: [] };
    case FieldType.TextField:
      return { ...base, defaultValue: '', maxLength: null };
    case FieldType.UrlField:
      return { ...base, defaultValue: '', maxLength: null, openTargetInNewWindow: false };
    default:
      return base;
  }
}

/* =========================================================================
 * Shared Tailwind class tokens
 * ========================================================================= */

const inputClasses =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm ' +
  'text-gray-900 shadow-sm placeholder:text-gray-400 ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-500 ' +
  'disabled:cursor-not-allowed disabled:bg-gray-100 disabled:text-gray-500';

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

const btnPrimaryClasses =
  'inline-flex items-center justify-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium ' +
  'text-white shadow-sm hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 ' +
  'focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:opacity-50 disabled:cursor-not-allowed ' +
  'transition-colors duration-150';

const btnSecondaryClasses =
  'inline-flex items-center justify-center rounded-md border border-gray-300 bg-white px-4 py-2 ' +
  'text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none ' +
  'focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 transition-colors duration-150';

const sectionClasses =
  'rounded-lg border border-gray-200 bg-white p-6 shadow-sm';

const sectionTitleClasses =
  'text-base font-semibold text-gray-900 mb-4';

const formRowClasses =
  'grid grid-cols-1 gap-4 sm:grid-cols-2 mb-4';

const singleColRowClasses =
  'mb-4';

/* =========================================================================
 * Component
 * ========================================================================= */

function AdminEntityFieldCreate(): React.JSX.Element {
  /* --- Route params --------------------------------------------------- */
  const { entityId = '' } = useParams();
  const navigate = useNavigate();

  /* --- Data fetching -------------------------------------------------- */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityError,
    error: entityFetchError,
  } = useEntity(entityId);

  const { data: rolesData, isLoading: rolesLoading } = useRoles();
  const createFieldMutation = useCreateField();

  /* --- Derived values ------------------------------------------------- */
  const roles: ErpRole[] = useMemo(
    () => (rolesData as { object?: ErpRole[] } | undefined)?.object ?? [],
    [rolesData],
  );

  const entityName: string = useMemo(
    () => (entity as Entity | undefined)?.name ?? '',
    [entity],
  );

  const entityLabel: string = useMemo(
    () => (entity as Entity | undefined)?.label ?? entityName,
    [entity, entityName],
  );

  const entityColor: string = useMemo(
    () => (entity as Entity | undefined)?.color ?? '#7c3aed',
    [entity],
  );

  /* --- Step control (1 = type picker, 2 = form) ----------------------- */
  const [step, setStep] = useState<1 | 2>(1);
  const [selectedFieldType, setSelectedFieldType] = useState<FieldType | null>(null);

  /* --- Form state ----------------------------------------------------- */
  const [fieldData, setFieldData] = useState<Record<string, unknown>>({});
  const [selectOptionsText, setSelectOptionsText] = useState('');
  const [multiSelectDefaultText, setMultiSelectDefaultText] = useState('');
  const [validationErrors, setValidationErrors] = useState<Record<string, string>>({});
  const [showSuccess, setShowSuccess] = useState(false);

  /* --- Field type selection handler ----------------------------------- */
  const handleSelectFieldType = useCallback((ft: FieldType): void => {
    setSelectedFieldType(ft);
    setFieldData(buildDefaultFieldData(ft));
    setSelectOptionsText('');
    setMultiSelectDefaultText('');
    setValidationErrors({});
    setStep(2);
  }, []);

  /* --- Back to type selection ----------------------------------------- */
  const handleBackToTypeSelection = useCallback((): void => {
    setStep(1);
    setSelectedFieldType(null);
    setFieldData({});
    setSelectOptionsText('');
    setMultiSelectDefaultText('');
    setValidationErrors({});
  }, []);

  /* --- Generic field property updater --------------------------------- */
  const updateProp = useCallback((key: string, value: unknown): void => {
    setFieldData((prev) => ({ ...prev, [key]: value }));
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
        const perms: FieldPermissions = {
          canRead: [...((prev.permissions as FieldPermissions | undefined)?.canRead ?? [])],
          canUpdate: [...((prev.permissions as FieldPermissions | undefined)?.canUpdate ?? [])],
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
    setFieldData((prev) => ({ ...prev, enableSecurity: !prev.enableSecurity }));
  }, []);

  /* --- Form submission ------------------------------------------------ */
  const handleSubmit = useCallback(
    (e?: React.FormEvent): void => {
      if (e) e.preventDefault();
      if (!selectedFieldType || !entityId) return;

      const errors: Record<string, string> = {};

      /* Common validation */
      if (!String(fieldData.name ?? '').trim()) {
        errors.name = 'Name is required';
      } else if (!/^[a-z_][a-z0-9_]*$/i.test(String(fieldData.name))) {
        errors.name = 'Name must start with a letter or underscore and contain only letters, digits, and underscores';
      }
      if (!String(fieldData.label ?? '').trim()) {
        errors.label = 'Label is required';
      }

      /* Check duplicate field names against entity */
      const existingFields = (entity as Entity | undefined)?.fields ?? [];
      const trimmedName = String(fieldData.name ?? '').trim().toLowerCase();
      if (existingFields.some((f) => f.name.toLowerCase() === trimmedName)) {
        errors.name = 'A field with this name already exists on this entity';
      }

      /* Build final field payload, handling select/multiselect options */
      const payload: Record<string, unknown> = { ...fieldData };

      if (
        selectedFieldType === FieldType.SelectField ||
        selectedFieldType === FieldType.MultiSelectField
      ) {
        const parsedOptions = parseSelectOptions(selectOptionsText);
        payload.options = parsedOptions;

        if (selectedFieldType === FieldType.MultiSelectField) {
          const defaults = multiSelectDefaultText
            .split('\n')
            .map((s) => s.trim())
            .filter(Boolean);
          const optionValues = new Set(parsedOptions.map((o) => o.value));
          const invalid = defaults.filter((d) => !optionValues.has(d));
          if (invalid.length > 0) {
            errors.defaultValue = `Invalid default value(s): ${invalid.join(', ')}`;
          } else {
            payload.defaultValue = defaults;
          }
        } else {
          const dv = String(payload.defaultValue ?? '');
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
      createFieldMutation.mutate(
        { entityId, field: payload as unknown as AnyField },
        {
          onSuccess: () => {
            setShowSuccess(true);
            setTimeout(() => navigate(`/admin/entities/${entityId}/fields`), 1500);
          },
        },
      );
    },
    [
      selectedFieldType,
      entityId,
      entity,
      fieldData,
      selectOptionsText,
      multiSelectDefaultText,
      createFieldMutation,
      navigate,
    ],
  );

  /* =====================================================================
   * Type-specific configuration section
   * =================================================================== */

  const typeSpecificContent = useMemo((): React.ReactNode => {
    if (!selectedFieldType) return null;

    switch (selectedFieldType) {
      /* ---- Auto Number ---- */
      case FieldType.AutoNumberField:
        return (
          <>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-defaultValue">
                  Default Value
                </label>
                <input
                  id="ff-defaultValue"
                  type="number"
                  className={inputClasses}
                  value={fieldData.defaultValue != null ? String(fieldData.defaultValue) : ''}
                  onChange={(e) =>
                    updateProp('defaultValue', e.target.value === '' ? null : parseInt(e.target.value, 10))
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-startingNumber">
                  Starting Number
                </label>
                <input
                  id="ff-startingNumber"
                  type="number"
                  className={inputClasses}
                  value={fieldData.startingNumber != null ? Number(fieldData.startingNumber) : 1}
                  onChange={(e) =>
                    updateProp('startingNumber', e.target.value === '' ? 1 : parseInt(e.target.value, 10))
                  }
                />
              </div>
            </div>
            <div className={singleColRowClasses}>
              <label className={labelClasses} htmlFor="ff-displayFormat">
                Display Format
              </label>
              <input
                id="ff-displayFormat"
                type="text"
                className={inputClasses}
                value={String(fieldData.displayFormat ?? '{0}')}
                placeholder="e.g. {0:00000}"
                onChange={(e) => updateProp('displayFormat', e.target.value)}
              />
              <p className="mt-1 text-xs text-gray-500">
                C# string format pattern. Example: {'{0:00000}'} produces 00001, 00002…
              </p>
            </div>
          </>
        );

      /* ---- Checkbox ---- */
      case FieldType.CheckboxField:
        return (
          <div className={singleColRowClasses}>
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
          </div>
        );

      /* ---- Currency ---- */
      case FieldType.CurrencyField:
        return (
          <>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-currencyDefaultValue">
                  Default Value
                </label>
                <input
                  id="ff-currencyDefaultValue"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.defaultValue != null ? String(fieldData.defaultValue) : ''}
                  onChange={(e) =>
                    updateProp('defaultValue', e.target.value === '' ? null : parseFloat(e.target.value))
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
                  value={(fieldData.currency as CurrencyType | undefined)?.code ?? 'USD'}
                  onChange={(e) => {
                    const sel = CURRENCY_OPTIONS.find((c) => c.code === e.target.value);
                    if (sel) {
                      updateProp('currency', {
                        code: sel.code,
                        symbol: sel.symbol,
                        symbolNative: sel.symbol,
                        name: sel.name,
                        namePlural: sel.name + 's',
                        decimalDigits: 2,
                        rounding: 0,
                      } as CurrencyType);
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
            </div>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-currencyMin">
                  Min Value
                </label>
                <input
                  id="ff-currencyMin"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.minValue != null ? String(fieldData.minValue) : ''}
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
                  value={fieldData.maxValue != null ? String(fieldData.maxValue) : ''}
                  onChange={(e) =>
                    updateProp('maxValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
            </div>
          </>
        );

      /* ---- Date ---- */
      case FieldType.DateField:
        return (
          <>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-dateDefault">
                  Default Value
                </label>
                <input
                  id="ff-dateDefault"
                  type="date"
                  className={inputClasses}
                  value={String(fieldData.defaultValue ?? '')}
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
                  value={String(fieldData.format ?? '')}
                  placeholder="e.g. yyyy-MMM-dd"
                  onChange={(e) => updateProp('format', e.target.value)}
                />
              </div>
            </div>
            <div className={singleColRowClasses}>
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
            </div>
          </>
        );

      /* ---- DateTime ---- */
      case FieldType.DateTimeField:
        return (
          <>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-datetimeDefault">
                  Default Value
                </label>
                <input
                  id="ff-datetimeDefault"
                  type="datetime-local"
                  className={inputClasses}
                  value={String(fieldData.defaultValue ?? '')}
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
                  value={String(fieldData.format ?? '')}
                  placeholder="e.g. yyyy-MMM-dd HH:mm"
                  onChange={(e) => updateProp('format', e.target.value)}
                />
              </div>
            </div>
            <div className={singleColRowClasses}>
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
            </div>
          </>
        );

      /* ---- Email ---- */
      case FieldType.EmailField:
        return (
          <div className={formRowClasses}>
            <div>
              <label className={labelClasses} htmlFor="ff-emailDefault">
                Default Value
              </label>
              <input
                id="ff-emailDefault"
                type="email"
                className={inputClasses}
                value={String(fieldData.defaultValue ?? '')}
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
                value={fieldData.maxLength != null ? String(fieldData.maxLength) : ''}
                onChange={(e) =>
                  updateProp('maxLength', e.target.value === '' ? null : parseInt(e.target.value, 10))
                }
              />
            </div>
          </div>
        );

      /* ---- File ---- */
      case FieldType.FileField:
        return (
          <div className={singleColRowClasses}>
            <label className={labelClasses} htmlFor="ff-fileDefault">
              Default Value
            </label>
            <input
              id="ff-fileDefault"
              type="text"
              className={inputClasses}
              value={String(fieldData.defaultValue ?? '')}
              placeholder="Default file path or URL"
              onChange={(e) => updateProp('defaultValue', e.target.value)}
            />
          </div>
        );

      /* ---- Geography ---- */
      case FieldType.GeographyField:
        return (
          <>
            <div className="mb-4 rounded-md border border-amber-300 bg-amber-50 p-3">
              <p className="text-sm text-amber-800">
                <strong>Note:</strong> Geography fields require PostGIS extension on the database.
                Ensure your datastore supports spatial queries.
              </p>
            </div>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-geoFormat">
                  Geography Format
                </label>
                <select
                  id="ff-geoFormat"
                  className={selectClasses}
                  value={String(fieldData.format ?? GeographyFieldFormat.GeoJSON)}
                  onChange={(e) => updateProp('format', parseInt(e.target.value, 10) as GeographyFieldFormat)}
                >
                  <option value={GeographyFieldFormat.GeoJSON}>GeoJSON</option>
                  <option value={GeographyFieldFormat.Text}>Text</option>
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
                  value={fieldData.srid != null ? Number(fieldData.srid) : 4326}
                  onChange={(e) =>
                    updateProp('srid', e.target.value === '' ? 4326 : parseInt(e.target.value, 10))
                  }
                />
                <p className="mt-1 text-xs text-gray-500">Spatial Reference System Identifier (default: 4326 WGS 84)</p>
              </div>
            </div>
          </>
        );

      /* ---- Html ---- */
      case FieldType.HtmlField:
        return (
          <div className={singleColRowClasses}>
            <label className={labelClasses} htmlFor="ff-htmlDefault">
              Default Value
            </label>
            <textarea
              id="ff-htmlDefault"
              rows={4}
              className={textareaClasses}
              value={String(fieldData.defaultValue ?? '')}
              placeholder="Default HTML content"
              onChange={(e) => updateProp('defaultValue', e.target.value)}
            />
          </div>
        );

      /* ---- Image ---- */
      case FieldType.ImageField:
        return (
          <div className={singleColRowClasses}>
            <label className={labelClasses} htmlFor="ff-imageDefault">
              Default Value
            </label>
            <input
              id="ff-imageDefault"
              type="text"
              className={inputClasses}
              value={String(fieldData.defaultValue ?? '')}
              placeholder="Default image path or URL"
              onChange={(e) => updateProp('defaultValue', e.target.value)}
            />
          </div>
        );

      /* ---- Multi-Line Text ---- */
      case FieldType.MultiLineTextField:
        return (
          <>
            <div className={singleColRowClasses}>
              <label className={labelClasses} htmlFor="ff-multilineDefault">
                Default Value
              </label>
              <textarea
                id="ff-multilineDefault"
                rows={4}
                className={textareaClasses}
                value={String(fieldData.defaultValue ?? '')}
                onChange={(e) => updateProp('defaultValue', e.target.value)}
              />
            </div>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-multilineMaxLen">
                  Max Length
                </label>
                <input
                  id="ff-multilineMaxLen"
                  type="number"
                  min={0}
                  className={inputClasses}
                  value={fieldData.maxLength != null ? String(fieldData.maxLength) : ''}
                  onChange={(e) =>
                    updateProp('maxLength', e.target.value === '' ? null : parseInt(e.target.value, 10))
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-multilineVisibleLines">
                  Visible Line Number
                </label>
                <input
                  id="ff-multilineVisibleLines"
                  type="number"
                  min={1}
                  className={inputClasses}
                  value={fieldData.visibleLineNumber != null ? String(fieldData.visibleLineNumber) : ''}
                  onChange={(e) =>
                    updateProp('visibleLineNumber', e.target.value === '' ? null : parseInt(e.target.value, 10))
                  }
                />
              </div>
            </div>
          </>
        );

      /* ---- Multi Select ---- */
      case FieldType.MultiSelectField:
        return (
          <>
            <div className={singleColRowClasses}>
              <label className={labelClasses} htmlFor="ff-msOptions">
                Options
              </label>
              <textarea
                id="ff-msOptions"
                rows={6}
                className={textareaClasses}
                value={selectOptionsText}
                placeholder={'One option per line:\nvalue\nvalue,label\nvalue,label,iconClass\nvalue,label,iconClass,color'}
                onChange={(e) => setSelectOptionsText(e.target.value)}
              />
              <p className="mt-1 text-xs text-gray-500">
                Enter one option per line. Format: value or value,label or value,label,iconClass or value,label,iconClass,color
              </p>
            </div>
            <div className={singleColRowClasses}>
              <label className={labelClasses} htmlFor="ff-msDefault">
                Default Values
              </label>
              <textarea
                id="ff-msDefault"
                rows={3}
                className={textareaClasses}
                value={multiSelectDefaultText}
                placeholder="One default value per line"
                onChange={(e) => setMultiSelectDefaultText(e.target.value)}
              />
              {validationErrors.defaultValue && (
                <p className="mt-1 text-xs text-red-600">{validationErrors.defaultValue}</p>
              )}
            </div>
          </>
        );

      /* ---- Number ---- */
      case FieldType.NumberField:
        return (
          <>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-numDefault">
                  Default Value
                </label>
                <input
                  id="ff-numDefault"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.defaultValue != null ? String(fieldData.defaultValue) : ''}
                  onChange={(e) =>
                    updateProp('defaultValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-numDecPlaces">
                  Decimal Places
                </label>
                <input
                  id="ff-numDecPlaces"
                  type="number"
                  min={0}
                  max={10}
                  className={inputClasses}
                  value={fieldData.decimalPlaces != null ? Number(fieldData.decimalPlaces) : 2}
                  onChange={(e) =>
                    updateProp('decimalPlaces', e.target.value === '' ? null : parseInt(e.target.value, 10))
                  }
                />
              </div>
            </div>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-numMin">
                  Min Value
                </label>
                <input
                  id="ff-numMin"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.minValue != null ? String(fieldData.minValue) : ''}
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
                  value={fieldData.maxValue != null ? String(fieldData.maxValue) : ''}
                  onChange={(e) =>
                    updateProp('maxValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
            </div>
          </>
        );

      /* ---- Password ---- */
      case FieldType.PasswordField:
        return (
          <>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-pwMinLen">
                  Min Length
                </label>
                <input
                  id="ff-pwMinLen"
                  type="number"
                  min={0}
                  className={inputClasses}
                  value={fieldData.minLength != null ? String(fieldData.minLength) : ''}
                  onChange={(e) =>
                    updateProp('minLength', e.target.value === '' ? null : parseInt(e.target.value, 10))
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-pwMaxLen">
                  Max Length
                </label>
                <input
                  id="ff-pwMaxLen"
                  type="number"
                  min={0}
                  className={inputClasses}
                  value={fieldData.maxLength != null ? String(fieldData.maxLength) : ''}
                  onChange={(e) =>
                    updateProp('maxLength', e.target.value === '' ? null : parseInt(e.target.value, 10))
                  }
                />
              </div>
            </div>
            <div className={singleColRowClasses}>
              <div className="flex items-center gap-2">
                <input
                  id="ff-pwEncrypted"
                  type="checkbox"
                  className={checkboxClasses}
                  checked={fieldData.encrypted === true}
                  onChange={(e) => updateProp('encrypted', e.target.checked)}
                />
                <label htmlFor="ff-pwEncrypted" className="text-sm text-gray-700">
                  Encrypted
                </label>
              </div>
              <p className="mt-1 text-xs text-gray-500">
                When enabled, the value is stored encrypted and cannot be retrieved in plain text.
              </p>
            </div>
          </>
        );

      /* ---- Percent ---- */
      case FieldType.PercentField:
        return (
          <>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-pctDefault">
                  Default Value
                </label>
                <input
                  id="ff-pctDefault"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.defaultValue != null ? String(fieldData.defaultValue) : ''}
                  onChange={(e) =>
                    updateProp('defaultValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
              <div>
                <label className={labelClasses} htmlFor="ff-pctDecPlaces">
                  Decimal Places
                </label>
                <input
                  id="ff-pctDecPlaces"
                  type="number"
                  min={0}
                  max={10}
                  className={inputClasses}
                  value={fieldData.decimalPlaces != null ? Number(fieldData.decimalPlaces) : 2}
                  onChange={(e) =>
                    updateProp('decimalPlaces', e.target.value === '' ? null : parseInt(e.target.value, 10))
                  }
                />
              </div>
            </div>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-pctMin">
                  Min Value
                </label>
                <input
                  id="ff-pctMin"
                  type="number"
                  step="any"
                  className={inputClasses}
                  value={fieldData.minValue != null ? String(fieldData.minValue) : ''}
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
                  value={fieldData.maxValue != null ? String(fieldData.maxValue) : ''}
                  onChange={(e) =>
                    updateProp('maxValue', e.target.value === '' ? null : parseFloat(e.target.value))
                  }
                />
              </div>
            </div>
          </>
        );

      /* ---- Phone ---- */
      case FieldType.PhoneField:
        return (
          <div className={formRowClasses}>
            <div>
              <label className={labelClasses} htmlFor="ff-phoneDefault">
                Default Value
              </label>
              <input
                id="ff-phoneDefault"
                type="tel"
                className={inputClasses}
                value={String(fieldData.defaultValue ?? '')}
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
                value={fieldData.maxLength != null ? String(fieldData.maxLength) : ''}
                onChange={(e) =>
                  updateProp('maxLength', e.target.value === '' ? null : parseInt(e.target.value, 10))
                }
              />
            </div>
          </div>
        );

      /* ---- Guid ---- */
      case FieldType.GuidField:
        return (
          <>
            <div className={singleColRowClasses}>
              <label className={labelClasses} htmlFor="ff-guidDefault">
                Default Value
              </label>
              <input
                id="ff-guidDefault"
                type="text"
                className={inputClasses}
                value={String(fieldData.defaultValue ?? '')}
                placeholder="e.g. 00000000-0000-0000-0000-000000000000"
                onChange={(e) => updateProp('defaultValue', e.target.value || null)}
              />
            </div>
            <div className={singleColRowClasses}>
              <div className="flex items-center gap-2">
                <input
                  id="ff-guidGenerateNew"
                  type="checkbox"
                  className={checkboxClasses}
                  checked={fieldData.generateNewId === true}
                  onChange={(e) => updateProp('generateNewId', e.target.checked)}
                />
                <label htmlFor="ff-guidGenerateNew" className="text-sm text-gray-700">
                  Auto-generate new GUID on record creation
                </label>
              </div>
            </div>
          </>
        );

      /* ---- Select ---- */
      case FieldType.SelectField:
        return (
          <>
            <div className={singleColRowClasses}>
              <label className={labelClasses} htmlFor="ff-selOptions">
                Options
              </label>
              <textarea
                id="ff-selOptions"
                rows={6}
                className={textareaClasses}
                value={selectOptionsText}
                placeholder={'One option per line:\nvalue\nvalue,label\nvalue,label,iconClass\nvalue,label,iconClass,color'}
                onChange={(e) => setSelectOptionsText(e.target.value)}
              />
              <p className="mt-1 text-xs text-gray-500">
                Enter one option per line. Format: value or value,label or value,label,iconClass or value,label,iconClass,color
              </p>
            </div>
            <div className={singleColRowClasses}>
              <label className={labelClasses} htmlFor="ff-selDefault">
                Default Value
              </label>
              <input
                id="ff-selDefault"
                type="text"
                className={inputClasses}
                value={String(fieldData.defaultValue ?? '')}
                placeholder="Must match one of the option values"
                onChange={(e) => updateProp('defaultValue', e.target.value)}
              />
              {validationErrors.defaultValue && (
                <p className="mt-1 text-xs text-red-600">{validationErrors.defaultValue}</p>
              )}
            </div>
          </>
        );

      /* ---- Text ---- */
      case FieldType.TextField:
        return (
          <div className={formRowClasses}>
            <div>
              <label className={labelClasses} htmlFor="ff-textDefault">
                Default Value
              </label>
              <input
                id="ff-textDefault"
                type="text"
                className={inputClasses}
                value={String(fieldData.defaultValue ?? '')}
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
                value={fieldData.maxLength != null ? String(fieldData.maxLength) : ''}
                onChange={(e) =>
                  updateProp('maxLength', e.target.value === '' ? null : parseInt(e.target.value, 10))
                }
              />
            </div>
          </div>
        );

      /* ---- Url ---- */
      case FieldType.UrlField:
        return (
          <>
            <div className={formRowClasses}>
              <div>
                <label className={labelClasses} htmlFor="ff-urlDefault">
                  Default Value
                </label>
                <input
                  id="ff-urlDefault"
                  type="url"
                  className={inputClasses}
                  value={String(fieldData.defaultValue ?? '')}
                  placeholder="https://example.com"
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
                  value={fieldData.maxLength != null ? String(fieldData.maxLength) : ''}
                  onChange={(e) =>
                    updateProp('maxLength', e.target.value === '' ? null : parseInt(e.target.value, 10))
                  }
                />
              </div>
            </div>
            <div className={singleColRowClasses}>
              <div className="flex items-center gap-2">
                <input
                  id="ff-urlNewWindow"
                  type="checkbox"
                  className={checkboxClasses}
                  checked={fieldData.openTargetInNewWindow === true}
                  onChange={(e) => updateProp('openTargetInNewWindow', e.target.checked)}
                />
                <label htmlFor="ff-urlNewWindow" className="text-sm text-gray-700">
                  Open link in new browser tab
                </label>
              </div>
            </div>
          </>
        );

      default:
        return null;
    }
  }, [
    selectedFieldType,
    fieldData,
    selectOptionsText,
    multiSelectDefaultText,
    validationErrors.defaultValue,
    updateProp,
  ]);

  /* =====================================================================
   * Permission grid section
   * =================================================================== */

  const permissionGrid = useMemo((): React.ReactNode => {
    if (!fieldData.enableSecurity || roles.length === 0) return null;

    const perms = (fieldData.permissions as FieldPermissions | undefined) ?? { canRead: [], canUpdate: [] };

    return (
      <div className="mt-4 overflow-x-auto">
        <table className="min-w-full divide-y divide-gray-200 text-sm">
          <thead className="bg-gray-50">
            <tr>
              <th scope="col" className="px-4 py-2 text-start font-medium text-gray-700">
                Role
              </th>
              <th scope="col" className="px-4 py-2 text-center font-medium text-gray-700">
                Read
              </th>
              <th scope="col" className="px-4 py-2 text-center font-medium text-gray-700">
                Update
              </th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100 bg-white">
            {roles.map((role) => (
              <tr key={role.id}>
                <td className="px-4 py-2 text-gray-900">{role.name}</td>
                <td className="px-4 py-2 text-center">
                  <input
                    type="checkbox"
                    className={checkboxClasses}
                    checked={perms.canRead.includes(role.id)}
                    onChange={() => togglePermission(role.id, 'canRead')}
                    aria-label={`Read permission for ${role.name}`}
                  />
                </td>
                <td className="px-4 py-2 text-center">
                  <input
                    type="checkbox"
                    className={checkboxClasses}
                    checked={perms.canUpdate.includes(role.id)}
                    onChange={() => togglePermission(role.id, 'canUpdate')}
                    aria-label={`Update permission for ${role.name}`}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  }, [fieldData.enableSecurity, fieldData.permissions, roles, togglePermission]);

  /* =====================================================================
   * Render — Loading / Error states
   * =================================================================== */

  if (entityLoading || rolesLoading) {
    return (
      <div className="flex items-center justify-center py-20" role="status" aria-label="Loading">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-t-transparent" />
        <span className="ms-3 text-sm text-gray-500">Loading entity data…</span>
      </div>
    );
  }

  if (entityError) {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-6 text-center" role="alert">
        <p className="text-sm font-medium text-red-800">
          Failed to load entity: {(entityFetchError as Error)?.message ?? 'Unknown error'}
        </p>
        <Link
          to="/admin/entities"
          className="mt-3 inline-block text-sm text-blue-600 underline hover:text-blue-800"
        >
          Back to Entities
        </Link>
      </div>
    );
  }

  /* =====================================================================
   * Render — Step 1: Field type selection
   * =================================================================== */

  if (step === 1) {
    return (
      <div className="mx-auto max-w-6xl space-y-6 px-4 py-6">
        {/* Breadcrumbs */}
        <nav aria-label="Breadcrumb" className="text-sm text-gray-500">
          <ol className="flex items-center gap-1.5">
            <li>
              <Link to="/admin/entities" className="hover:text-blue-600 transition-colors duration-150">
                Entities
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li>
              <Link
                to={`/admin/entities/${entityId}/fields`}
                className="hover:text-blue-600 transition-colors duration-150"
              >
                {entityLabel || 'Entity'}
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li className="font-medium text-gray-900" aria-current="page">
              Create Field
            </li>
          </ol>
        </nav>

        {/* Page header */}
        <div className="flex items-center gap-3">
          {entityColor && (
            <span
              className="inline-block h-3 w-3 rounded-full"
              style={{ backgroundColor: entityColor }}
              aria-hidden="true"
            />
          )}
          <div>
            <h1 className="text-xl font-bold text-gray-900">Create Field</h1>
            <p className="text-sm text-gray-500">
              Entity: <span className="font-medium text-gray-700">{entityLabel}</span>
            </p>
          </div>
        </div>

        {/* Instruction */}
        <h2 className="text-base font-semibold text-gray-800">Select a field type</h2>

        {/* Field type cards grid */}
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4">
          {FIELD_TYPE_CARDS.map((card) => (
            <button
              key={card.type}
              type="button"
              data-testid={`field-type-${card.label.toLowerCase().replace(/[\s-]+/g, '-')}`}
              className={
                'flex flex-col items-center gap-2 rounded-lg border border-gray-200 bg-white p-5 ' +
                'text-center shadow-sm transition-all duration-150 ' +
                'hover:border-blue-400 hover:shadow-md focus-visible:outline-none focus-visible:ring-2 ' +
                'focus-visible:ring-blue-500 focus-visible:ring-offset-2'
              }
              onClick={() => handleSelectFieldType(card.type)}
              aria-label={`Create ${card.label} field`}
            >
              <span className="text-2xl" aria-hidden="true">{card.icon}</span>
              <span className="text-sm font-semibold text-gray-900">{card.label}</span>
              <span className="text-xs text-gray-500">{card.description}</span>
            </button>
          ))}
        </div>

        {/* Back link */}
        <div>
          <Link
            to={`/admin/entities/${entityId}/fields`}
            className="text-sm text-gray-500 hover:text-blue-600 transition-colors duration-150"
          >
            ← Back to fields list
          </Link>
        </div>
      </div>
    );
  }

  /* =====================================================================
   * Render — Step 2: Field creation form
   * =================================================================== */

  return (
      <div className="mx-auto max-w-4xl space-y-6 px-4 py-6">
      {showSuccess && (
        <div className="mb-4 rounded-md bg-green-50 p-4" role="status" aria-live="polite">
          <p className="text-sm font-medium text-green-800" data-testid="success-notification">Field created successfully. Redirecting…</p>
        </div>
      )}
      {/* Breadcrumbs */}
      <nav aria-label="Breadcrumb" className="text-sm text-gray-500">
        <ol className="flex items-center gap-1.5">
          <li>
            <Link to="/admin/entities" className="hover:text-blue-600 transition-colors duration-150">
              Entities
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li>
            <Link
              to={`/admin/entities/${entityId}/fields`}
              className="hover:text-blue-600 transition-colors duration-150"
            >
              {entityLabel || 'Entity'}
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li className="font-medium text-gray-900" aria-current="page">
            Create {selectedFieldType ? getFieldTypeName(selectedFieldType) : ''} Field
          </li>
        </ol>
      </nav>

      {/* Page header */}
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          {entityColor && (
            <span
              className="inline-block h-3 w-3 rounded-full"
              style={{ backgroundColor: entityColor }}
              aria-hidden="true"
            />
          )}
          <div>
            <h1 className="text-xl font-bold text-gray-900">
              Create {selectedFieldType ? getFieldTypeName(selectedFieldType) : ''} Field
            </h1>
            <p className="text-sm text-gray-500">
              Entity: <span className="font-medium text-gray-700">{entityLabel}</span>
            </p>
          </div>
        </div>
        <button
          type="button"
          className={btnSecondaryClasses}
          onClick={handleBackToTypeSelection}
        >
          Change Type
        </button>
      </div>

      {/* Mutation error banner */}
      {createFieldMutation.isError && (
        <div className="rounded-lg border border-red-200 bg-red-50 p-4" role="alert">
          <p className="text-sm text-red-800">
            <strong>Error:</strong>{' '}
            {(createFieldMutation.error as Error)?.message ?? 'Failed to create field. Please try again.'}
          </p>
        </div>
      )}

      <form onSubmit={handleSubmit} noValidate>
        {/* ============================================================
         * Section: General Properties
         * ============================================================ */}
        <section className={`${sectionClasses} mb-6`} aria-labelledby="section-general">
          <h2 id="section-general" className={sectionTitleClasses}>
            General
          </h2>

          <div className={formRowClasses}>
            <div>
              <label className={labelClasses} htmlFor="ff-name">
                Name <span className="text-red-500">*</span>
              </label>
              <input
                id="ff-name"
                name="name"
                type="text"
                className={inputClasses}
                value={String(fieldData.name ?? '')}
                placeholder="field_name"
                onChange={(e) => updateProp('name', e.target.value)}
                autoComplete="off"
                required
              />
              {validationErrors.name && (
                <p className="mt-1 text-xs text-red-600">{validationErrors.name}</p>
              )}
              <p className="mt-1 text-xs text-gray-500">
                Alphanumeric and underscores only. Cannot be changed after creation.
              </p>
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
                value={String(fieldData.label ?? '')}
                placeholder="Field Label"
                onChange={(e) => updateProp('label', e.target.value)}
                required
              />
              {validationErrors.label && (
                <p className="mt-1 text-xs text-red-600">{validationErrors.label}</p>
              )}
            </div>
          </div>

          <div className={formRowClasses}>
            <div>
              <label className={labelClasses} htmlFor="ff-placeholder">
                Placeholder Text
              </label>
              <input
                id="ff-placeholder"
                type="text"
                className={inputClasses}
                value={String(fieldData.placeholderText ?? '')}
                onChange={(e) => updateProp('placeholderText', e.target.value)}
              />
            </div>
            <div>
              <label className={labelClasses} htmlFor="ff-helpText">
                Help Text
              </label>
              <input
                id="ff-helpText"
                type="text"
                className={inputClasses}
                value={String(fieldData.helpText ?? '')}
                onChange={(e) => updateProp('helpText', e.target.value)}
              />
            </div>
          </div>

          <div className={singleColRowClasses}>
            <label className={labelClasses} htmlFor="ff-description">
              Description
            </label>
            <textarea
              id="ff-description"
              rows={2}
              className={textareaClasses}
              value={String(fieldData.description ?? '')}
              onChange={(e) => updateProp('description', e.target.value)}
            />
          </div>

          <div className="flex flex-wrap gap-x-6 gap-y-3">
            <div className="flex items-center gap-2">
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
            <div className="flex items-center gap-2">
              <input
                id="ff-unique"
                type="checkbox"
                className={checkboxClasses}
                checked={fieldData.unique === true}
                onChange={(e) => updateProp('unique', e.target.checked)}
                disabled={selectedFieldType === FieldType.GeographyField}
              />
              <label
                htmlFor="ff-unique"
                className={`text-sm ${selectedFieldType === FieldType.GeographyField ? 'text-gray-400' : 'text-gray-700'}`}
              >
                Unique{selectedFieldType === FieldType.GeographyField ? ' (not supported for Geography)' : ''}
              </label>
            </div>
            <div className="flex items-center gap-2">
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
            <div className="flex items-center gap-2">
              <input
                id="ff-system"
                type="checkbox"
                className={checkboxClasses}
                checked={fieldData.system === true}
                onChange={(e) => updateProp('system', e.target.checked)}
              />
              <label htmlFor="ff-system" className="text-sm text-gray-700">
                System
              </label>
            </div>
          </div>
        </section>

        {/* ============================================================
         * Section: Type-Specific Configuration
         * ============================================================ */}
        {typeSpecificContent && (
          <section className={`${sectionClasses} mb-6`} aria-labelledby="section-type-config">
            <h2 id="section-type-config" className={sectionTitleClasses}>
              {selectedFieldType ? getFieldTypeName(selectedFieldType) : 'Field'} Configuration
            </h2>
            {typeSpecificContent}
          </section>
        )}

        {/* ============================================================
         * Section: Field-Level Security
         * ============================================================ */}
        <section className={`${sectionClasses} mb-6`} aria-labelledby="section-security">
          <h2 id="section-security" className={sectionTitleClasses}>
            API Security
          </h2>

          <div className={singleColRowClasses}>
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
            <p className="mt-1 text-xs text-gray-500">
              When enabled, the field will be accessible only to roles with explicit Read or Update permission.
            </p>
          </div>

          {permissionGrid}
        </section>

        {/* ============================================================
         * Action bar
         * ============================================================ */}
        <div className="flex items-center gap-3">
          <button
            type="submit"
            className={btnPrimaryClasses}
            disabled={createFieldMutation.isPending}
          >
            {createFieldMutation.isPending ? 'Creating…' : 'Create Field'}
          </button>
          <Link
            to={`/admin/entities/${entityId}/fields`}
            className={btnSecondaryClasses}
          >
            Cancel
          </Link>
        </div>
      </form>
    </div>
  );
}

export default AdminEntityFieldCreate;
