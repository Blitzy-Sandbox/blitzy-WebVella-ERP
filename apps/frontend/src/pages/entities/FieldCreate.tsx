import React, { useState, useCallback, useMemo, type FormEvent } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, post, type ApiResponse } from '../../api/client';
import DynamicForm, { type FormValidation } from '../../components/forms/DynamicForm';
import {
  FieldType,
  GeographyFieldFormat,
  type Entity,
  type Field,
  type FieldPermissions,
  type SelectOption,
  type CurrencyType,
} from '../../types/entity';
import type { ErpRole } from '../../types/user';

/* ═══════════════════════════════════════════════════════════════
   FIELD TYPE CARD DEFINITIONS
   ═══════════════════════════════════════════════════════════════ */

/** Descriptor for each selectable field type rendered on the Step 1 card grid. */
interface FieldTypeCard {
  readonly type: number;
  readonly name: string;
  readonly description: string;
  readonly icon: string;
}

/**
 * Static array of the 20 user-selectable field types.
 * FieldType is a `const enum` and values are inlined at compile-time so we
 * cannot iterate over it — a hand-maintained list is required.
 * RelationField (20) is excluded because relations are managed separately.
 */
const FIELD_TYPE_CARDS: ReadonlyArray<FieldTypeCard> = [
  { type: FieldType.AutoNumberField,  name: 'Auto Number',  description: 'Automatically incremented number', icon: 'fa-sort-numeric-up' },
  { type: FieldType.CheckboxField,    name: 'Checkbox',     description: 'Boolean true/false value',       icon: 'fa-check-square' },
  { type: FieldType.CurrencyField,    name: 'Currency',     description: 'Monetary value with currency',   icon: 'fa-dollar-sign' },
  { type: FieldType.DateField,        name: 'Date',         description: 'Date without time component',    icon: 'fa-calendar' },
  { type: FieldType.DateTimeField,    name: 'Date & Time',  description: 'Date with time component',       icon: 'fa-calendar-alt' },
  { type: FieldType.EmailField,       name: 'Email',        description: 'Valid email address',             icon: 'fa-at' },
  { type: FieldType.FileField,        name: 'File',         description: 'Uploaded file reference',         icon: 'fa-file' },
  { type: FieldType.HtmlField,        name: 'HTML',         description: 'Rich HTML content',               icon: 'fa-code' },
  { type: FieldType.ImageField,       name: 'Image',        description: 'Image file reference',            icon: 'fa-image' },
  { type: FieldType.MultiSelectField, name: 'Multiselect',  description: 'Multiple choice from list',       icon: 'fa-list' },
  { type: FieldType.NumberField,      name: 'Number',       description: 'Numeric value',                   icon: 'fa-hashtag' },
  { type: FieldType.PasswordField,    name: 'Password',     description: 'Encrypted password value',        icon: 'fa-lock' },
  { type: FieldType.PercentField,     name: 'Percent',      description: 'Percentage value',                icon: 'fa-percentage' },
  { type: FieldType.PhoneField,       name: 'Phone',        description: 'Phone number',                    icon: 'fa-phone' },
  { type: FieldType.GuidField,        name: 'Unique ID',    description: 'Globally unique identifier',      icon: 'fa-fingerprint' },
  { type: FieldType.SelectField,      name: 'Select',       description: 'Single choice from list',         icon: 'fa-caret-square-down' },
  { type: FieldType.TextField,        name: 'Text',         description: 'Short text value',                icon: 'fa-font' },
  { type: FieldType.MultiLineTextField, name: 'Textarea',   description: 'Multi-line text value',           icon: 'fa-paragraph' },
  { type: FieldType.UrlField,         name: 'URL',          description: 'Web address link',                icon: 'fa-link' },
  { type: FieldType.GeographyField,   name: 'Geography',    description: 'Geospatial coordinates',          icon: 'fa-map-marker-alt' },
];

/* ═══════════════════════════════════════════════════════════════
   CURRENCY LIST
   ═══════════════════════════════════════════════════════════════ */

/** Comprehensive subset of world currencies for the CurrencyField type selector. */
const CURRENCIES: ReadonlyArray<{ code: string; name: string }> = [
  { code: 'AED', name: 'UAE Dirham' },
  { code: 'ARS', name: 'Argentine Peso' },
  { code: 'AUD', name: 'Australian Dollar' },
  { code: 'BGN', name: 'Bulgarian Lev' },
  { code: 'BRL', name: 'Brazilian Real' },
  { code: 'CAD', name: 'Canadian Dollar' },
  { code: 'CHF', name: 'Swiss Franc' },
  { code: 'CLP', name: 'Chilean Peso' },
  { code: 'CNY', name: 'Chinese Yuan' },
  { code: 'COP', name: 'Colombian Peso' },
  { code: 'CZK', name: 'Czech Koruna' },
  { code: 'DKK', name: 'Danish Krone' },
  { code: 'EGP', name: 'Egyptian Pound' },
  { code: 'EUR', name: 'Euro' },
  { code: 'GBP', name: 'British Pound' },
  { code: 'HKD', name: 'Hong Kong Dollar' },
  { code: 'HUF', name: 'Hungarian Forint' },
  { code: 'IDR', name: 'Indonesian Rupiah' },
  { code: 'ILS', name: 'Israeli Shekel' },
  { code: 'INR', name: 'Indian Rupee' },
  { code: 'JPY', name: 'Japanese Yen' },
  { code: 'KRW', name: 'South Korean Won' },
  { code: 'MXN', name: 'Mexican Peso' },
  { code: 'MYR', name: 'Malaysian Ringgit' },
  { code: 'NGN', name: 'Nigerian Naira' },
  { code: 'NOK', name: 'Norwegian Krone' },
  { code: 'NZD', name: 'New Zealand Dollar' },
  { code: 'PEN', name: 'Peruvian Sol' },
  { code: 'PHP', name: 'Philippine Peso' },
  { code: 'PKR', name: 'Pakistani Rupee' },
  { code: 'PLN', name: 'Polish Zloty' },
  { code: 'RON', name: 'Romanian Leu' },
  { code: 'RUB', name: 'Russian Ruble' },
  { code: 'SAR', name: 'Saudi Riyal' },
  { code: 'SEK', name: 'Swedish Krona' },
  { code: 'SGD', name: 'Singapore Dollar' },
  { code: 'THB', name: 'Thai Baht' },
  { code: 'TRY', name: 'Turkish Lira' },
  { code: 'TWD', name: 'Taiwan Dollar' },
  { code: 'UAH', name: 'Ukrainian Hryvnia' },
  { code: 'USD', name: 'US Dollar' },
  { code: 'VND', name: 'Vietnamese Dong' },
  { code: 'ZAR', name: 'South African Rand' },
];

/* ═══════════════════════════════════════════════════════════════
   OPTION PARSER
   ═══════════════════════════════════════════════════════════════ */

/**
 * Parse newline-separated "value,label,iconClass,color" text to SelectOption[].
 * Mirrors the C# monolith's parsing logic from `create-field.cshtml.cs`.
 */
function parseOptions(text: string): SelectOption[] {
  if (!text.trim()) return [];
  return text
    .split('\n')
    .filter((line) => line.trim())
    .map((line) => {
      const parts = line.split(',');
      return {
        value: parts[0]?.trim() ?? '',
        label: parts[1]?.trim() ?? parts[0]?.trim() ?? '',
        iconClass: parts[2]?.trim() ?? '',
        color: parts[3]?.trim() ?? '',
      };
    });
}

/** Human-readable field type display name. */
function getFieldTypeName(type: number): string {
  const card = FIELD_TYPE_CARDS.find((c) => c.type === type);
  return card?.name ?? 'Unknown';
}

/* ═══════════════════════════════════════════════════════════════
   SHARED TAILWIND CSS CLASS CONSTANTS
   ═══════════════════════════════════════════════════════════════ */

const inputCls =
  'block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm ' +
  'placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500';
const checkboxCls =
  'size-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500';
const labelCls = 'block text-sm font-medium text-gray-700 mb-1';
const fieldGroupCls = 'mb-4';

/* ═══════════════════════════════════════════════════════════════
   FIELD CREATE COMPONENT
   ═══════════════════════════════════════════════════════════════ */

/**
 * FieldCreate — two-step field creation page.
 *
 * Route: `/entities/:entityId/fields/create`
 *
 * Step 1 — Field Type Selection: grid of 20 field type cards (icon, name,
 * description). Clicking a card advances to Step 2.
 *
 * Step 2 — Field Configuration: dynamic form showing common fields (name,
 * label, description, etc.) and type-specific configuration (min/max values,
 * select options, currency, etc.). Includes a permission grid when
 * EnableSecurity is checked. Submits via
 * `POST /v1/entity-management/entities/:entityId/fields`.
 *
 * Replaces the Razor `create-field-select.cshtml` and `create-field.cshtml`
 * pages from the SDK plugin.
 */
export default function FieldCreate(): React.ReactElement {
  /* ─── Router hooks ─── */
  const { entityId } = useParams<{ entityId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ─── Step management ─── */
  const [step, setStep] = useState<1 | 2>(1);
  const [selectedType, setSelectedType] = useState<number | null>(null);

  /* ─── Common field state ─── */
  const [name, setName] = useState('');
  const [label, setLabel] = useState('');
  const [fieldId, setFieldId] = useState('');
  const [required, setRequired] = useState(false);
  const [description, setDescription] = useState('');
  const [unique, setUnique] = useState(false);
  const [helpText, setHelpText] = useState('');
  const [system, setSystem] = useState(false);
  const [placeholderText, setPlaceholderText] = useState('');
  const [searchable, setSearchable] = useState(false);
  const [enableSecurity, setEnableSecurity] = useState(false);

  /* ─── Type-specific state ─── */
  const [defaultValue, setDefaultValue] = useState('');
  const [displayFormat, setDisplayFormat] = useState('');
  const [startingNumber, setStartingNumber] = useState('1');
  const [minValue, setMinValue] = useState('');
  const [maxValue, setMaxValue] = useState('');
  const [currencyCode, setCurrencyCode] = useState('USD');
  const [useCurrentTimeAsDefaultValue, setUseCurrentTimeAsDefaultValue] = useState(false);
  const [dateFormat, setDateFormat] = useState('');
  const [maxLength, setMaxLength] = useState('');
  const [minLength, setMinLength] = useState('');
  const [decimalPlaces, setDecimalPlaces] = useState('2');
  const [encrypted, setEncrypted] = useState(true);
  const [generateNewId, setGenerateNewId] = useState(false);
  const [openTargetInNewWindow, setOpenTargetInNewWindow] = useState(false);
  const [optionsText, setOptionsText] = useState('');
  const [geographySrid, setGeographySrid] = useState('4326');
  const [geographyFormat, setGeographyFormat] = useState<string>(
    String(GeographyFieldFormat.GeoJSON),
  );

  /* ─── Permission grid state: { roleId: ["read","update"] } ─── */
  const [permissions, setPermissions] = useState<Record<string, string[]>>({});

  /* ─── Form validation ─── */
  const [validation, setValidation] = useState<FormValidation>({
    message: '',
    errors: [],
  });

  /* ═══ Server queries ═══ */
  const entityQuery = useQuery<ApiResponse<Entity>>({
    queryKey: ['entities', entityId],
    queryFn: () => get<Entity>(`/entity-management/entities/${entityId}`),
    enabled: !!entityId,
  });

  const rolesQuery = useQuery<ApiResponse<ErpRole[]>>({
    queryKey: ['roles'],
    queryFn: () => get<ErpRole[]>('/identity/roles'),
  });

  /* ─── Derived data ─── */
  const entity = entityQuery.data?.object ?? null;
  const roles = rolesQuery.data?.object ?? [];

  /* ═══ Reset type-specific state when switching types ═══ */
  const handleSelectType = useCallback((type: number) => {
    setSelectedType(type);
    setStep(2);
    /* Reset all type-specific state to defaults */
    setDefaultValue('');
    setDisplayFormat('');
    setStartingNumber('1');
    setMinValue('');
    setMaxValue('');
    setCurrencyCode('USD');
    setUseCurrentTimeAsDefaultValue(false);
    setDateFormat('');
    setMaxLength('');
    setMinLength('');
    setDecimalPlaces('2');
    setEncrypted(true);
    setGenerateNewId(false);
    setOpenTargetInNewWindow(false);
    setOptionsText('');
    setGeographySrid('4326');
    setGeographyFormat(String(GeographyFieldFormat.GeoJSON));
    /* Reset common state */
    setName('');
    setLabel('');
    setFieldId('');
    setRequired(false);
    setDescription('');
    setUnique(false);
    setHelpText('');
    setSystem(false);
    setPlaceholderText('');
    setSearchable(false);
    setEnableSecurity(false);
    setPermissions({});
    setValidation({ message: '', errors: [] });
  }, []);

  /* ═══ Permission grid toggle ═══ */
  const togglePermission = useCallback(
    (roleId: string, perm: 'read' | 'update') => {
      setPermissions((prev) => {
        const current = prev[roleId] ?? [];
        const has = current.includes(perm);
        return {
          ...prev,
          [roleId]: has ? current.filter((p) => p !== perm) : [...current, perm],
        };
      });
    },
    [],
  );

  /* ═══ Build field creation payload ═══ */
  const buildPayload = useCallback((): Record<string, unknown> => {
    if (selectedType === null) return {};

    const base: Record<string, unknown> = {
      fieldType: selectedType,
      name,
      label,
      required,
      description,
      unique,
      helpText,
      system,
      placeholderText,
      searchable,
      enableSecurity,
    };

    /* Optional user-supplied ID */
    if (fieldId.trim()) {
      base.id = fieldId.trim();
    }

    /* Permission serialisation */
    if (enableSecurity) {
      const canRead: string[] = [];
      const canUpdate: string[] = [];
      Object.entries(permissions).forEach(([roleId, perms]) => {
        if (perms.includes('read')) canRead.push(roleId);
        if (perms.includes('update')) canUpdate.push(roleId);
      });
      base.permissions = { canRead, canUpdate } satisfies FieldPermissions;
    }

    /* Type-specific augmentation */
    switch (selectedType) {
      case FieldType.AutoNumberField:
        base.defaultValue = defaultValue !== '' ? Number(defaultValue) : 0;
        base.startingNumber = startingNumber !== '' ? Number(startingNumber) : 1;
        base.displayFormat = displayFormat;
        break;

      case FieldType.CheckboxField:
        base.defaultValue = defaultValue === 'true';
        break;

      case FieldType.CurrencyField:
        base.defaultValue = defaultValue !== '' ? Number(defaultValue) : null;
        base.minValue = minValue !== '' ? Number(minValue) : null;
        base.maxValue = maxValue !== '' ? Number(maxValue) : null;
        base.currency = { code: currencyCode } as Partial<CurrencyType>;
        break;

      case FieldType.DateField:
      case FieldType.DateTimeField:
        base.useCurrentTimeAsDefaultValue = useCurrentTimeAsDefaultValue;
        base.format = dateFormat || null;
        if (!useCurrentTimeAsDefaultValue) {
          base.defaultValue = defaultValue || null;
        }
        break;

      case FieldType.EmailField:
        base.defaultValue = defaultValue || null;
        base.maxLength = maxLength !== '' ? Number(maxLength) : null;
        break;

      case FieldType.FileField:
      case FieldType.ImageField:
        base.defaultValue = defaultValue || null;
        break;

      case FieldType.HtmlField:
        base.defaultValue = defaultValue || null;
        break;

      case FieldType.MultiSelectField:
        base.options = parseOptions(optionsText);
        base.defaultValue = defaultValue
          ? defaultValue.split('\n').filter((v) => v.trim())
          : [];
        break;

      case FieldType.SelectField:
        base.options = parseOptions(optionsText);
        base.defaultValue = defaultValue || null;
        break;

      case FieldType.NumberField:
      case FieldType.PercentField:
        base.defaultValue = defaultValue !== '' ? Number(defaultValue) : null;
        base.minValue = minValue !== '' ? Number(minValue) : null;
        base.maxValue = maxValue !== '' ? Number(maxValue) : null;
        base.decimalPlaces = decimalPlaces !== '' ? Number(decimalPlaces) : 2;
        break;

      case FieldType.PasswordField:
        base.minLength = minLength !== '' ? Number(minLength) : null;
        base.maxLength = maxLength !== '' ? Number(maxLength) : null;
        base.encrypted = encrypted;
        break;

      case FieldType.PhoneField:
        base.defaultValue = defaultValue || null;
        base.maxLength = maxLength !== '' ? Number(maxLength) : null;
        break;

      case FieldType.GuidField:
        base.defaultValue = defaultValue || null;
        base.generateNewId = generateNewId;
        break;

      case FieldType.TextField:
        base.defaultValue = defaultValue || null;
        base.maxLength = maxLength !== '' ? Number(maxLength) : null;
        break;

      case FieldType.MultiLineTextField:
        base.defaultValue = defaultValue || null;
        base.maxLength = maxLength !== '' ? Number(maxLength) : null;
        break;

      case FieldType.UrlField:
        base.defaultValue = defaultValue || null;
        base.maxLength = maxLength !== '' ? Number(maxLength) : null;
        base.openTargetInNewWindow = openTargetInNewWindow;
        break;

      case FieldType.GeographyField:
        base.defaultValue = defaultValue || null;
        base.maxLength = maxLength !== '' ? Number(maxLength) : null;
        base.srid = geographySrid !== '' ? Number(geographySrid) : 4326;
        base.format = geographyFormat !== '' ? Number(geographyFormat) : null;
        break;

      default:
        base.defaultValue = defaultValue || null;
        break;
    }

    return base;
  }, [
    selectedType, name, label, fieldId, required, description, unique,
    helpText, system, placeholderText, searchable, enableSecurity,
    permissions, defaultValue, displayFormat, startingNumber,
    minValue, maxValue, currencyCode, useCurrentTimeAsDefaultValue,
    dateFormat, maxLength, minLength, decimalPlaces, encrypted,
    generateNewId, openTargetInNewWindow, optionsText,
    geographySrid, geographyFormat,
  ]);

  /* ═══ Create mutation ═══ */
  const createMutation = useMutation<ApiResponse<Field>, Error, Record<string, unknown>>({
    mutationFn: (payload: Record<string, unknown>) =>
      post<Field>(
        `/entity-management/entities/${entityId}/fields`,
        payload,
      ),
    onSuccess: (res) => {
      if (res.success) {
        queryClient.invalidateQueries({ queryKey: ['entities', entityId] });
        queryClient.invalidateQueries({ queryKey: ['entities'] });
        navigate(`/entities/${entityId}/fields`);
      } else {
        setValidation({
          message: res.message ?? 'Field creation failed.',
          errors: (res.errors ?? []).map((e) => ({
            propertyName: e.key ?? '',
            message: e.message || e.value || '',
          })),
        });
      }
    },
    onError: (err) => {
      setValidation({
        message: err.message || 'An unexpected error occurred.',
        errors: [],
      });
    },
  });

  /* ═══ Submit handler ═══ */
  const handleSubmit = useCallback(
    (e: FormEvent) => {
      e.preventDefault();
      setValidation({ message: '', errors: [] });
      const payload = buildPayload();
      createMutation.mutate(payload);
    },
    [buildPayload, createMutation],
  );

  /* ═══ Memoized field type card data (for the Step 1 grid) ═══ */
  const fieldTypeCards = useMemo(() => FIELD_TYPE_CARDS, []);

  /* ═══ Loading / error states ═══ */
  if (entityQuery.isLoading) {
    return (
      <div className="flex items-center justify-center p-16">
        <div className="size-8 animate-spin rounded-full border-4 border-indigo-600 border-t-transparent" />
        <span className="ms-3 text-sm text-gray-500">Loading entity data…</span>
      </div>
    );
  }

  if (entityQuery.isError) {
    return (
      <div className="mx-auto max-w-4xl p-6">
        <div className="rounded-md bg-red-50 p-4 text-sm text-red-700">
          Failed to load entity data. Please try again later.
        </div>
      </div>
    );
  }

  if (!entity) {
    return (
      <div className="mx-auto max-w-4xl p-6">
        <div className="rounded-md bg-yellow-50 p-4 text-sm text-yellow-700">
          Entity not found.
        </div>
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════════
     RENDER — TYPE-SPECIFIC FORM SECTION
     ═══════════════════════════════════════════════════════════════ */

  function renderTypeSpecificSection(): React.ReactNode {
    if (selectedType === null) return null;

    switch (selectedType) {
      /* ── AutoNumber ── */
      case FieldType.AutoNumberField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="number"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
                placeholder="0"
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-starting-number">Starting Number</label>
              <input
                id="fc-starting-number"
                type="number"
                value={startingNumber}
                onChange={(e) => setStartingNumber(e.target.value)}
                className={inputCls}
                placeholder="1"
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-display-format">Display Format</label>
              <input
                id="fc-display-format"
                type="text"
                value={displayFormat}
                onChange={(e) => setDisplayFormat(e.target.value)}
                className={inputCls}
                placeholder="e.g. {0:00000}"
              />
              <p className="mt-1 text-xs text-gray-500">
                Use .NET string format pattern. Leave empty for plain number.
              </p>
            </div>
          </>
        );

      /* ── Checkbox ── */
      case FieldType.CheckboxField:
        return (
          <div className={fieldGroupCls + ' flex items-center gap-2'}>
            <input
              id="fc-default-value"
              type="checkbox"
              checked={defaultValue === 'true'}
              onChange={(e) => setDefaultValue(e.target.checked ? 'true' : 'false')}
              className={checkboxCls}
            />
            <label htmlFor="fc-default-value" className="text-sm text-gray-700">
              Default Checked
            </label>
          </div>
        );

      /* ── Currency ── */
      case FieldType.CurrencyField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="number"
                step="any"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-min-value">Min Value</label>
              <input
                id="fc-min-value"
                type="number"
                step="any"
                value={minValue}
                onChange={(e) => setMinValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-max-value">Max Value</label>
              <input
                id="fc-max-value"
                type="number"
                step="any"
                value={maxValue}
                onChange={(e) => setMaxValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-currency">Currency</label>
              <select
                id="fc-currency"
                value={currencyCode}
                onChange={(e) => setCurrencyCode(e.target.value)}
                className={inputCls}
              >
                {CURRENCIES.map((c) => (
                  <option key={c.code} value={c.code}>
                    {c.code} — {c.name}
                  </option>
                ))}
              </select>
            </div>
          </>
        );

      /* ── Date ── */
      case FieldType.DateField:
        return (
          <>
            <div className={fieldGroupCls + ' flex items-center gap-2'}>
              <input
                id="fc-use-current-time"
                type="checkbox"
                checked={useCurrentTimeAsDefaultValue}
                onChange={(e) => setUseCurrentTimeAsDefaultValue(e.target.checked)}
                className={checkboxCls}
              />
              <label htmlFor="fc-use-current-time" className="text-sm text-gray-700">
                Use current date as default value
              </label>
            </div>
            {!useCurrentTimeAsDefaultValue && (
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
                <input
                  id="fc-default-value"
                  type="text"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  className={inputCls}
                  placeholder="e.g. 2024-01-15"
                />
              </div>
            )}
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-date-format">Format</label>
              <input
                id="fc-date-format"
                type="text"
                value={dateFormat}
                onChange={(e) => setDateFormat(e.target.value)}
                className={inputCls}
                placeholder="yyyy-MMM-dd"
              />
            </div>
          </>
        );

      /* ── DateTime ── */
      case FieldType.DateTimeField:
        return (
          <>
            <div className={fieldGroupCls + ' flex items-center gap-2'}>
              <input
                id="fc-use-current-time"
                type="checkbox"
                checked={useCurrentTimeAsDefaultValue}
                onChange={(e) => setUseCurrentTimeAsDefaultValue(e.target.checked)}
                className={checkboxCls}
              />
              <label htmlFor="fc-use-current-time" className="text-sm text-gray-700">
                Use current date &amp; time as default value
              </label>
            </div>
            {!useCurrentTimeAsDefaultValue && (
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
                <input
                  id="fc-default-value"
                  type="text"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  className={inputCls}
                  placeholder="e.g. 2024-01-15T10:30:00"
                />
              </div>
            )}
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-date-format">Format</label>
              <input
                id="fc-date-format"
                type="text"
                value={dateFormat}
                onChange={(e) => setDateFormat(e.target.value)}
                className={inputCls}
                placeholder="yyyy-MMM-dd HH:mm"
              />
            </div>
          </>
        );

      /* ── Email ── */
      case FieldType.EmailField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="email"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-max-length">Max Length</label>
              <input
                id="fc-max-length"
                type="number"
                min="0"
                value={maxLength}
                onChange={(e) => setMaxLength(e.target.value)}
                className={inputCls}
              />
            </div>
          </>
        );

      /* ── File ── */
      case FieldType.FileField:
        return (
          <div className={fieldGroupCls}>
            <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
            <input
              id="fc-default-value"
              type="text"
              value={defaultValue}
              onChange={(e) => setDefaultValue(e.target.value)}
              className={inputCls}
              placeholder="File path or URL"
            />
          </div>
        );

      /* ── HTML ── */
      case FieldType.HtmlField:
        return (
          <div className={fieldGroupCls}>
            <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
            <textarea
              id="fc-default-value"
              value={defaultValue}
              onChange={(e) => setDefaultValue(e.target.value)}
              rows={4}
              className={inputCls}
              placeholder="HTML content"
            />
          </div>
        );

      /* ── Image ── */
      case FieldType.ImageField:
        return (
          <div className={fieldGroupCls}>
            <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
            <input
              id="fc-default-value"
              type="text"
              value={defaultValue}
              onChange={(e) => setDefaultValue(e.target.value)}
              className={inputCls}
              placeholder="Image path or URL"
            />
          </div>
        );

      /* ── Multiselect ── */
      case FieldType.MultiSelectField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-options">Options</label>
              <textarea
                id="fc-options"
                value={optionsText}
                onChange={(e) => setOptionsText(e.target.value)}
                rows={6}
                className={inputCls}
                placeholder={'value,label,iconClass,color\nvalue2,label2,,'}
              />
              <p className="mt-1 text-xs text-gray-500">
                One option per line. Format: value,label,iconClass,color (iconClass and color optional)
              </p>
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Values</label>
              <textarea
                id="fc-default-value"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                rows={3}
                className={inputCls}
                placeholder="One value per line"
              />
              <p className="mt-1 text-xs text-gray-500">
                Enter default selected values, one per line. Must match option values above.
              </p>
            </div>
          </>
        );

      /* ── Number ── */
      case FieldType.NumberField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="number"
                step="any"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-min-value">Min Value</label>
              <input
                id="fc-min-value"
                type="number"
                step="any"
                value={minValue}
                onChange={(e) => setMinValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-max-value">Max Value</label>
              <input
                id="fc-max-value"
                type="number"
                step="any"
                value={maxValue}
                onChange={(e) => setMaxValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-decimal-places">Decimal Places</label>
              <input
                id="fc-decimal-places"
                type="number"
                min="0"
                max="10"
                value={decimalPlaces}
                onChange={(e) => setDecimalPlaces(e.target.value)}
                className={inputCls}
              />
            </div>
          </>
        );

      /* ── Password ── */
      case FieldType.PasswordField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-min-length">Min Length</label>
              <input
                id="fc-min-length"
                type="number"
                min="0"
                value={minLength}
                onChange={(e) => setMinLength(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-max-length">Max Length</label>
              <input
                id="fc-max-length"
                type="number"
                min="0"
                value={maxLength}
                onChange={(e) => setMaxLength(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls + ' flex items-center gap-2'}>
              <input
                id="fc-encrypted"
                type="checkbox"
                checked={encrypted}
                onChange={(e) => setEncrypted(e.target.checked)}
                className={checkboxCls}
              />
              <label htmlFor="fc-encrypted" className="text-sm text-gray-700">
                Encrypted
              </label>
            </div>
          </>
        );

      /* ── Percent ── */
      case FieldType.PercentField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="number"
                step="any"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-min-value">Min Value</label>
              <input
                id="fc-min-value"
                type="number"
                step="any"
                value={minValue}
                onChange={(e) => setMinValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-max-value">Max Value</label>
              <input
                id="fc-max-value"
                type="number"
                step="any"
                value={maxValue}
                onChange={(e) => setMaxValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-decimal-places">Decimal Places</label>
              <input
                id="fc-decimal-places"
                type="number"
                min="0"
                max="10"
                value={decimalPlaces}
                onChange={(e) => setDecimalPlaces(e.target.value)}
                className={inputCls}
              />
            </div>
          </>
        );

      /* ── Phone ── */
      case FieldType.PhoneField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="tel"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-max-length">Max Length</label>
              <input
                id="fc-max-length"
                type="number"
                min="0"
                value={maxLength}
                onChange={(e) => setMaxLength(e.target.value)}
                className={inputCls}
              />
            </div>
          </>
        );

      /* ── Guid ── */
      case FieldType.GuidField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="text"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
                placeholder="00000000-0000-0000-0000-000000000000"
              />
            </div>
            <div className={fieldGroupCls + ' flex items-center gap-2'}>
              <input
                id="fc-generate-new-id"
                type="checkbox"
                checked={generateNewId}
                onChange={(e) => setGenerateNewId(e.target.checked)}
                className={checkboxCls}
              />
              <label htmlFor="fc-generate-new-id" className="text-sm text-gray-700">
                Generate new ID for each record
              </label>
            </div>
          </>
        );

      /* ── Select ── */
      case FieldType.SelectField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-options">Options</label>
              <textarea
                id="fc-options"
                value={optionsText}
                onChange={(e) => setOptionsText(e.target.value)}
                rows={6}
                className={inputCls}
                placeholder={'value,label,iconClass,color\nvalue2,label2,,'}
              />
              <p className="mt-1 text-xs text-gray-500">
                One option per line. Format: value,label,iconClass,color (iconClass and color optional)
              </p>
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="text"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
                placeholder="Must match one of the option values above"
              />
            </div>
          </>
        );

      /* ── Text ── */
      case FieldType.TextField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="text"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-max-length">Max Length</label>
              <input
                id="fc-max-length"
                type="number"
                min="0"
                value={maxLength}
                onChange={(e) => setMaxLength(e.target.value)}
                className={inputCls}
              />
            </div>
          </>
        );

      /* ── Textarea (MultiLineText) ── */
      case FieldType.MultiLineTextField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <textarea
                id="fc-default-value"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                rows={4}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-max-length">Max Length</label>
              <input
                id="fc-max-length"
                type="number"
                min="0"
                value={maxLength}
                onChange={(e) => setMaxLength(e.target.value)}
                className={inputCls}
              />
            </div>
          </>
        );

      /* ── URL ── */
      case FieldType.UrlField:
        return (
          <>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="url"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
                placeholder="https://"
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-max-length">Max Length</label>
              <input
                id="fc-max-length"
                type="number"
                min="0"
                value={maxLength}
                onChange={(e) => setMaxLength(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls + ' flex items-center gap-2'}>
              <input
                id="fc-open-new-window"
                type="checkbox"
                checked={openTargetInNewWindow}
                onChange={(e) => setOpenTargetInNewWindow(e.target.checked)}
                className={checkboxCls}
              />
              <label htmlFor="fc-open-new-window" className="text-sm text-gray-700">
                Open target in new window
              </label>
            </div>
          </>
        );

      /* ── Geography ── */
      case FieldType.GeographyField:
        return (
          <>
            <div className="mb-4 rounded-md bg-amber-50 p-3 text-sm text-amber-800">
              <strong>Note:</strong> Geography fields require PostGIS extension on the
              database server. In the serverless architecture, geographic data is stored
              as GeoJSON in DynamoDB.
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-default-value">Default Value</label>
              <input
                id="fc-default-value"
                type="text"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
                placeholder="GeoJSON or coordinate text"
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-max-length">Max Length</label>
              <input
                id="fc-max-length"
                type="number"
                min="0"
                value={maxLength}
                onChange={(e) => setMaxLength(e.target.value)}
                className={inputCls}
              />
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-srid">SRID</label>
              <input
                id="fc-srid"
                type="number"
                value={geographySrid}
                onChange={(e) => setGeographySrid(e.target.value)}
                className={inputCls}
                placeholder="4326"
              />
              <p className="mt-1 text-xs text-gray-500">
                Spatial Reference System Identifier. Default is 4326 (WGS 84).
              </p>
            </div>
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fc-geo-format">Format</label>
              <select
                id="fc-geo-format"
                value={geographyFormat}
                onChange={(e) => setGeographyFormat(e.target.value)}
                className={inputCls}
              >
                <option value={String(GeographyFieldFormat.GeoJSON)}>GeoJSON</option>
                <option value={String(GeographyFieldFormat.Text)}>Text</option>
              </select>
            </div>
          </>
        );

      default:
        return null;
    }
  }

  /* ═══════════════════════════════════════════════════════════════
     RENDER — PERMISSION GRID
     ═══════════════════════════════════════════════════════════════ */

  function renderPermissionGrid(): React.ReactNode {
    if (!enableSecurity) return null;

    if (rolesQuery.isLoading) {
      return (
        <div className="flex items-center gap-2 p-4">
          <div className="size-5 animate-spin rounded-full border-2 border-indigo-600 border-t-transparent" />
          <span className="text-sm text-gray-500">Loading roles…</span>
        </div>
      );
    }

    if (!roles.length) {
      return (
        <p className="text-sm text-gray-500">
          No roles available. Create roles first to configure field-level security.
        </p>
      );
    }

    return (
      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-gray-200 text-sm">
          <thead>
            <tr className="bg-gray-50">
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
          <tbody className="divide-y divide-gray-100">
            {roles.map((role) => {
              const rolePerms = permissions[role.id] ?? [];
              return (
                <tr key={role.id}>
                  <td className="px-4 py-2 font-medium text-gray-900">
                    {role.name}
                    {role.description && (
                      <span className="ms-2 text-xs text-gray-400">{role.description}</span>
                    )}
                  </td>
                  <td className="px-4 py-2 text-center">
                    <input
                      type="checkbox"
                      checked={rolePerms.includes('read')}
                      onChange={() => togglePermission(role.id, 'read')}
                      className={checkboxCls}
                      aria-label={`${role.name} read permission`}
                    />
                  </td>
                  <td className="px-4 py-2 text-center">
                    <input
                      type="checkbox"
                      checked={rolePerms.includes('update')}
                      onChange={() => togglePermission(role.id, 'update')}
                      className={checkboxCls}
                      aria-label={`${role.name} update permission`}
                    />
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════════
     RENDER — MAIN JSX
     ═══════════════════════════════════════════════════════════════ */

  return (
    <section className="mx-auto max-w-4xl p-6">
      {/* ─── Entity admin sub-nav ─── */}
      <nav className="mb-6 flex gap-4 border-b border-gray-200 pb-3" aria-label="Entity admin navigation">
        <Link
          to={`/entities/${entityId}`}
          className="text-sm font-medium text-gray-500 hover:text-gray-700"
        >
          Entity Details
        </Link>
        <Link
          to={`/entities/${entityId}/fields`}
          className="text-sm font-medium text-indigo-600"
          aria-current="page"
        >
          Fields
        </Link>
        <Link
          to={`/entities/${entityId}/relations`}
          className="text-sm font-medium text-gray-500 hover:text-gray-700"
        >
          Relations
        </Link>
      </nav>

      {/* ═══════════════════════════════════════════════════════════
         STEP 1 — FIELD TYPE SELECTION
         ═══════════════════════════════════════════════════════════ */}
      {step === 1 && (
        <>
          <div className="mb-6">
            <h1 className="text-xl font-semibold text-gray-900">
              Create Field
            </h1>
            <p className="mt-1 text-sm text-gray-500">
              Select a field type for entity <strong>{entity.label ?? entity.name}</strong>
            </p>
          </div>

          <div
            className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3"
            role="list"
            aria-label="Field type selection"
          >
            {fieldTypeCards.map((card) => (
              <button
                key={card.type}
                type="button"
                role="listitem"
                onClick={() => handleSelectType(card.type)}
                className={
                  'flex items-start gap-3 rounded-lg border border-gray-200 bg-white p-4 text-start ' +
                  'shadow-sm transition-colors hover:border-indigo-400 hover:bg-indigo-50 ' +
                  'focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 ' +
                  'focus-visible:outline-indigo-600'
                }
              >
                <span
                  className="flex size-10 shrink-0 items-center justify-center rounded-md bg-indigo-100 text-indigo-600"
                  aria-hidden="true"
                >
                  <i className={`fas ${card.icon}`} />
                </span>
                <div className="min-w-0">
                  <span className="block text-sm font-medium text-gray-900">
                    {card.name}
                  </span>
                  <span className="block text-xs text-gray-500">
                    {card.description}
                  </span>
                </div>
              </button>
            ))}
          </div>
        </>
      )}

      {/* ═══════════════════════════════════════════════════════════
         STEP 2 — FIELD CONFIGURATION FORM
         ═══════════════════════════════════════════════════════════ */}
      {step === 2 && selectedType !== null && (
        <>
          {/* ─── Page header with actions ─── */}
          <div className="mb-6 flex items-center justify-between">
            <div>
              <h1 className="text-xl font-semibold text-gray-900">
                Create Field
              </h1>
              <p className="mt-1 text-sm text-gray-500">
                <span className="inline-block rounded bg-indigo-100 px-2 py-0.5 text-xs font-medium text-indigo-800">
                  {getFieldTypeName(selectedType)}
                </span>
                {' '}on entity <strong>{entity.label ?? entity.name}</strong>
              </p>
            </div>
            <div className="flex gap-2">
              <button
                type="submit"
                form="field-create-form"
                disabled={createMutation.isPending}
                className={
                  'inline-flex items-center rounded-md px-4 py-2 text-sm font-medium text-white shadow-sm ' +
                  'bg-indigo-600 hover:bg-indigo-700 focus-visible:outline focus-visible:outline-2 ' +
                  'focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-50'
                }
              >
                {createMutation.isPending ? 'Creating…' : 'Create Field'}
              </button>
              <button
                type="button"
                onClick={() => setStep(1)}
                className={
                  'inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm ' +
                  'font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline ' +
                  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600'
                }
              >
                Back
              </button>
            </div>
          </div>

          {/* ─── Form ─── */}
          <DynamicForm
            id="field-create-form"
            validation={validation}
            onSubmit={handleSubmit}
          >
            {/* ═══ General Section ═══ */}
            <fieldset className="mb-8 rounded-lg border border-gray-200 p-6">
              <legend className="px-2 text-base font-semibold text-gray-900">General</legend>

              {/* Name */}
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fc-name">
                  Name <span className="text-red-500">*</span>
                </label>
                <input
                  id="fc-name"
                  type="text"
                  required
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  className={inputCls}
                  placeholder="field_name"
                />
              </div>

              {/* Label */}
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fc-label">Label</label>
                <input
                  id="fc-label"
                  type="text"
                  value={label}
                  onChange={(e) => setLabel(e.target.value)}
                  className={inputCls}
                />
              </div>

              {/* Field Id (optional) */}
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fc-field-id">Field Id</label>
                <input
                  id="fc-field-id"
                  type="text"
                  value={fieldId}
                  onChange={(e) => setFieldId(e.target.value)}
                  className={inputCls}
                  placeholder="Auto-generated if empty"
                />
                <p className="mt-1 text-xs text-gray-500">
                  Leave empty for auto-generated GUID. Provide a valid GUID to specify a custom ID.
                </p>
              </div>

              {/* Required */}
              <div className={fieldGroupCls + ' flex items-center gap-2'}>
                <input
                  id="fc-required"
                  type="checkbox"
                  checked={required}
                  onChange={(e) => setRequired(e.target.checked)}
                  className={checkboxCls}
                />
                <label htmlFor="fc-required" className="text-sm text-gray-700">Required</label>
              </div>

              {/* Description */}
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fc-description">Description</label>
                <textarea
                  id="fc-description"
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  rows={2}
                  className={inputCls}
                />
              </div>

              {/* Unique (disabled for GeographyField) */}
              <div className={fieldGroupCls + ' flex items-center gap-2'}>
                <input
                  id="fc-unique"
                  type="checkbox"
                  checked={unique}
                  onChange={(e) => setUnique(e.target.checked)}
                  disabled={selectedType === FieldType.GeographyField}
                  className={checkboxCls + (selectedType === FieldType.GeographyField ? ' opacity-50' : '')}
                />
                <label
                  htmlFor="fc-unique"
                  className={'text-sm text-gray-700' + (selectedType === FieldType.GeographyField ? ' opacity-50' : '')}
                >
                  Unique
                  {selectedType === FieldType.GeographyField && (
                    <span className="ms-1 text-xs text-gray-400">(not available for geography fields)</span>
                  )}
                </label>
              </div>

              {/* Help Text */}
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fc-help-text">Help Text</label>
                <input
                  id="fc-help-text"
                  type="text"
                  value={helpText}
                  onChange={(e) => setHelpText(e.target.value)}
                  className={inputCls}
                />
              </div>

              {/* System */}
              <div className={fieldGroupCls + ' flex items-center gap-2'}>
                <input
                  id="fc-system"
                  type="checkbox"
                  checked={system}
                  onChange={(e) => setSystem(e.target.checked)}
                  className={checkboxCls}
                />
                <label htmlFor="fc-system" className="text-sm text-gray-700">System</label>
              </div>

              {/* Placeholder Text */}
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fc-placeholder">Placeholder Text</label>
                <input
                  id="fc-placeholder"
                  type="text"
                  value={placeholderText}
                  onChange={(e) => setPlaceholderText(e.target.value)}
                  className={inputCls}
                />
              </div>

              {/* Searchable */}
              <div className={fieldGroupCls + ' flex items-center gap-2'}>
                <input
                  id="fc-searchable"
                  type="checkbox"
                  checked={searchable}
                  onChange={(e) => setSearchable(e.target.checked)}
                  className={checkboxCls}
                />
                <label htmlFor="fc-searchable" className="text-sm text-gray-700">Searchable</label>
              </div>
            </fieldset>

            {/* ═══ Type-Specific Section ═══ */}
            <fieldset className="mb-8 rounded-lg border border-gray-200 p-6">
              <legend className="px-2 text-base font-semibold text-gray-900">
                Type Specific — {getFieldTypeName(selectedType)}
              </legend>
              {renderTypeSpecificSection()}
            </fieldset>

            {/* ═══ API Security Section ═══ */}
            <fieldset className="mb-8 rounded-lg border border-gray-200 p-6">
              <legend className="px-2 text-base font-semibold text-gray-900">API Security</legend>

              <div className={fieldGroupCls + ' flex items-center gap-2'}>
                <input
                  id="fc-enable-security"
                  type="checkbox"
                  checked={enableSecurity}
                  onChange={(e) => setEnableSecurity(e.target.checked)}
                  className={checkboxCls}
                />
                <label htmlFor="fc-enable-security" className="text-sm text-gray-700">
                  Enable Security
                </label>
              </div>

              {renderPermissionGrid()}
            </fieldset>
          </DynamicForm>
        </>
      )}
    </section>
  );
}
