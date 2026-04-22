import React, { useState, useEffect, useCallback, useMemo, type FormEvent } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, put, type ApiResponse } from '../../api/client';
import DynamicForm, { type FormValidation } from '../../components/forms/DynamicForm';
import {
  FieldType,
  GeographyFieldFormat,
  type Entity,
  type Field,
  type AnyField,
  type FieldPermissions,
  type SelectOption,
  type CurrencyType,
} from '../../types/entity';
import type { ErpRole } from '../../types/user';

/**
 * Common world currencies for the CurrencyField type selector.
 * Comprehensive subset covering major economies; the server validates the code.
 */
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

/** Serialize SelectOption array to newline-separated "value,label,iconClass,color" text. */
function serializeOptions(opts: SelectOption[]): string {
  return opts
    .map((o) => [o.value, o.label, o.iconClass, o.color].join(','))
    .join('\n');
}

/** Parse newline-separated "value,label,iconClass,color" text back to SelectOption array. */
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

/** Human-readable field type display names. */
function getFieldTypeName(type: number): string {
  const names: Record<number, string> = {
    [FieldType.AutoNumberField]: 'Auto Number',
    [FieldType.CheckboxField]: 'Checkbox',
    [FieldType.CurrencyField]: 'Currency',
    [FieldType.DateField]: 'Date',
    [FieldType.DateTimeField]: 'Date & Time',
    [FieldType.EmailField]: 'Email',
    [FieldType.FileField]: 'File',
    [FieldType.HtmlField]: 'HTML',
    [FieldType.ImageField]: 'Image',
    [FieldType.MultiSelectField]: 'Multiselect',
    [FieldType.NumberField]: 'Number',
    [FieldType.PasswordField]: 'Password',
    [FieldType.PercentField]: 'Percent',
    [FieldType.PhoneField]: 'Phone',
    [FieldType.GuidField]: 'Unique Identifier',
    [FieldType.SelectField]: 'Select',
    [FieldType.MultiLineTextField]: 'Textarea',
    [FieldType.TextField]: 'Text',
    [FieldType.UrlField]: 'URL',
    [FieldType.GeographyField]: 'Geography',
  };
  return names[type] ?? 'Unknown';
}

/* ─── Shared Tailwind CSS class constants ─── */
const inputCls =
  'block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm ' +
  'placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500';
const readOnlyCls =
  'block w-full rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-700';
const checkboxCls =
  'size-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500';
const labelCls = 'block text-sm font-medium text-gray-700 mb-1';
const fieldGroupCls = 'mb-4';

/**
 * FieldManage — edit-field page component.
 *
 * Route: `/entities/:entityId/fields/:fieldId/manage`
 *
 * Replaces the Razor `manage-field.cshtml` / `manage-field.cshtml.cs` from the
 * SDK plugin. Loads the parent entity (with its fields array), finds the target
 * field, pre-populates every common + type-specific property, and submits the
 * update via `PUT /v1/entity-management/entities/:entityId/fields/:fieldId`.
 */
export default function FieldManage(): React.ReactElement {
  /* ─── Router ─── */
  const { entityId, fieldId } = useParams<{ entityId: string; fieldId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ─── Server queries ─── */
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
  const field: AnyField | null = useMemo(() => {
    if (!entity?.fields || !fieldId) return null;
    const found = entity.fields.find((f: Field) => f.id === fieldId);
    return (found as AnyField) ?? null;
  }, [entity, fieldId]);

  /* ─── Common field state ─── */
  const [name, setName] = useState('');
  const [label, setLabel] = useState('');
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

  /* ─────────────────────────────────────────────────────────
     Pre-populate form state from loaded field data.
     Runs once when the field object resolves from the query.
     ───────────────────────────────────────────────────────── */
  useEffect(() => {
    if (!field) return;

    /* Common properties */
    setName(field.name ?? '');
    setLabel(field.label ?? '');
    setRequired(!!field.required);
    setDescription(field.description ?? '');
    setUnique(!!field.unique);
    setHelpText(field.helpText ?? '');
    setSystem(!!field.system);
    setPlaceholderText(field.placeholderText ?? '');
    setSearchable(!!field.searchable);
    setEnableSecurity(!!field.enableSecurity);

    /* Permission grid — typed as FieldPermissions for canRead/canUpdate arrays */
    if (field.permissions) {
      const fp: FieldPermissions = field.permissions;
      const map: Record<string, string[]> = {};
      const canRead = fp.canRead ?? [];
      const canUpdate = fp.canUpdate ?? [];
      const allRoleIds = new Set([...canRead, ...canUpdate]);
      allRoleIds.forEach((roleId) => {
        const perms: string[] = [];
        if (canRead.includes(roleId)) perms.push('read');
        if (canUpdate.includes(roleId)) perms.push('update');
        map[roleId] = perms;
      });
      setPermissions(map);
    }

    /* Type-specific pre-population */
    const af = field as AnyField;
    switch (af.fieldType) {
      case FieldType.AutoNumberField:
        setDefaultValue(af.defaultValue != null ? String(af.defaultValue) : '');
        setDisplayFormat(af.displayFormat ?? '');
        setStartingNumber(af.startingNumber != null ? String(af.startingNumber) : '1');
        break;

      case FieldType.CheckboxField:
        setDefaultValue(af.defaultValue != null ? String(af.defaultValue).toLowerCase() : 'false');
        break;

      case FieldType.CurrencyField:
        setDefaultValue(af.defaultValue != null ? String(af.defaultValue) : '');
        setMinValue(af.minValue != null ? String(af.minValue) : '');
        setMaxValue(af.maxValue != null ? String(af.maxValue) : '');
        if (af.currency && typeof af.currency === 'object') {
          setCurrencyCode((af.currency as CurrencyType).code ?? 'USD');
        } else if (typeof af.currency === 'string') {
          setCurrencyCode(af.currency);
        }
        break;

      case FieldType.DateField:
      case FieldType.DateTimeField:
        setUseCurrentTimeAsDefaultValue(!!af.useCurrentTimeAsDefaultValue);
        setDateFormat(af.format ?? '');
        if (!af.useCurrentTimeAsDefaultValue) {
          setDefaultValue(af.defaultValue != null ? String(af.defaultValue) : '');
        }
        break;

      case FieldType.EmailField:
      case FieldType.PhoneField:
        setDefaultValue(
          af.defaultValue != null && af.defaultValue !== '' ? String(af.defaultValue) : '',
        );
        setMaxLength(af.maxLength != null ? String(af.maxLength) : '');
        break;

      case FieldType.FileField:
      case FieldType.ImageField:
      case FieldType.HtmlField:
        setDefaultValue(
          af.defaultValue != null && af.defaultValue !== '' ? String(af.defaultValue) : '',
        );
        break;

      case FieldType.MultiSelectField:
        if (Array.isArray(af.options)) {
          setOptionsText(serializeOptions(af.options));
        }
        if (Array.isArray(af.defaultValue)) {
          setDefaultValue(af.defaultValue.join('\n'));
        } else if (af.defaultValue != null) {
          setDefaultValue(String(af.defaultValue));
        }
        break;

      case FieldType.SelectField:
        if (Array.isArray(af.options)) {
          setOptionsText(serializeOptions(af.options));
        }
        setDefaultValue(af.defaultValue != null ? String(af.defaultValue) : '');
        break;

      case FieldType.NumberField:
      case FieldType.PercentField:
        setDefaultValue(af.defaultValue != null ? String(af.defaultValue) : '');
        setMinValue(af.minValue != null ? String(af.minValue) : '');
        setMaxValue(af.maxValue != null ? String(af.maxValue) : '');
        setDecimalPlaces(af.decimalPlaces != null ? String(af.decimalPlaces) : '2');
        break;

      case FieldType.PasswordField:
        setMinLength(af.minLength != null ? String(af.minLength) : '');
        setMaxLength(af.maxLength != null ? String(af.maxLength) : '');
        setEncrypted(af.encrypted !== false);
        break;

      case FieldType.GuidField:
        setDefaultValue(af.defaultValue != null ? String(af.defaultValue) : '');
        setGenerateNewId(!!af.generateNewId);
        break;

      case FieldType.TextField:
        setDefaultValue(af.defaultValue != null ? String(af.defaultValue) : '');
        setMaxLength(af.maxLength != null ? String(af.maxLength) : '');
        break;

      case FieldType.MultiLineTextField:
        setDefaultValue(af.defaultValue != null ? String(af.defaultValue) : '');
        setMaxLength(af.maxLength != null ? String(af.maxLength) : '');
        break;

      case FieldType.UrlField:
        setDefaultValue(af.defaultValue != null ? String(af.defaultValue) : '');
        setMaxLength(af.maxLength != null ? String(af.maxLength) : '');
        setOpenTargetInNewWindow(!!af.openTargetInNewWindow);
        break;

      case FieldType.GeographyField:
        setDefaultValue(af.defaultValue != null ? String(af.defaultValue) : '');
        setMaxLength(af.maxLength != null ? String(af.maxLength) : '');
        setGeographySrid(af.srid != null ? String(af.srid) : '4326');
        setGeographyFormat(
          af.format != null ? String(af.format) : String(GeographyFieldFormat.GeoJSON),
        );
        break;

      default:
        setDefaultValue(
          (field as Field & { defaultValue?: unknown }).defaultValue != null
            ? String((field as Field & { defaultValue?: unknown }).defaultValue)
            : '',
        );
        break;
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [field]);

  /* ─── Permission grid toggle ─── */
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

  /* ─── Build field-type-specific payload for PUT ─── */
  const buildPayload = useCallback((): Record<string, unknown> => {
    if (!field) return {};

    /* Common properties (name is NOT sent — immutable after creation) */
    const base: Record<string, unknown> = {
      id: field.id,
      fieldType: field.fieldType,
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

    /* Permission serialisation */
    if (enableSecurity) {
      const canRead: string[] = [];
      const canUpdate: string[] = [];
      Object.entries(permissions).forEach(([roleId, perms]) => {
        if (perms.includes('read')) canRead.push(roleId);
        if (perms.includes('update')) canUpdate.push(roleId);
      });
      base.permissions = { canRead, canUpdate };
    }

    /* Type-specific augmentation */
    switch (field.fieldType) {
      case FieldType.AutoNumberField:
        base.displayFormat = displayFormat;
        /* defaultValue & startingNumber are read-only after creation */
        break;

      case FieldType.CheckboxField:
        base.defaultValue = defaultValue === 'true';
        break;

      case FieldType.CurrencyField:
        base.defaultValue = defaultValue !== '' ? Number(defaultValue) : null;
        base.minValue = minValue !== '' ? Number(minValue) : null;
        base.maxValue = maxValue !== '' ? Number(maxValue) : null;
        base.currency = { code: currencyCode };
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
      case FieldType.PhoneField:
        base.defaultValue = defaultValue || null;
        base.maxLength = maxLength !== '' ? Number(maxLength) : null;
        break;

      case FieldType.FileField:
      case FieldType.ImageField:
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

      case FieldType.GuidField:
        base.defaultValue = defaultValue || null;
        base.generateNewId = generateNewId;
        break;

      case FieldType.TextField:
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
    field, label, required, description, unique, helpText, system,
    placeholderText, searchable, enableSecurity, permissions,
    defaultValue, displayFormat, minValue, maxValue, currencyCode,
    useCurrentTimeAsDefaultValue, dateFormat, maxLength, minLength,
    decimalPlaces, encrypted, generateNewId, openTargetInNewWindow,
    optionsText, geographySrid, geographyFormat,
  ]);

  /* ─── Update mutation ─── */
  const updateMutation = useMutation<ApiResponse<Field>, Error, Record<string, unknown>>({
    mutationFn: (payload: Record<string, unknown>) =>
      put<Field>(
        `/entity-management/entities/${entityId}/fields/${fieldId}`,
        payload,
      ),
    onSuccess: (res) => {
      if (res.success) {
        queryClient.invalidateQueries({ queryKey: ['entities', entityId] });
        queryClient.invalidateQueries({ queryKey: ['entities'] });
        navigate(`/entities/${entityId}/fields/${fieldId}`);
      } else {
        setValidation({
          message: res.message ?? 'Update failed.',
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

  /* ─── Submit handler ─── */
  const handleSubmit = useCallback(
    (e: FormEvent) => {
      e.preventDefault();
      setValidation({ message: '', errors: [] });
      const payload = buildPayload();
      updateMutation.mutate(payload);
    },
    [buildPayload, updateMutation],
  );

  /* ─── Loading / error states ─── */
  if (entityQuery.isLoading || rolesQuery.isLoading) {
    return (
      <div className="flex items-center justify-center p-16">
        <div className="size-8 animate-spin rounded-full border-4 border-indigo-600 border-t-transparent" />
        <span className="ms-3 text-sm text-gray-500">Loading field data…</span>
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

  if (!entity || !field) {
    return (
      <div className="mx-auto max-w-4xl p-6">
        <div className="rounded-md bg-yellow-50 p-4 text-sm text-yellow-700">
          {!entity ? 'Entity not found.' : 'Field not found on this entity.'}
        </div>
      </div>
    );
  }

  /* ─── JSX ─── */
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

      {/* ─── Page header ─── */}
      <div className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold text-gray-900">
            Manage Field
          </h1>
          <p className="mt-1 text-sm text-gray-500">
            <span className="inline-block rounded bg-indigo-100 px-2 py-0.5 text-xs font-medium text-indigo-800">
              {getFieldTypeName(field.fieldType)}
            </span>
            {' '}on entity <strong>{entity.label ?? entity.name}</strong>
          </p>
        </div>
        <div className="flex gap-2">
          <button
            type="submit"
            form="field-manage-form"
            disabled={updateMutation.isPending}
            className={
              'inline-flex items-center rounded-md px-4 py-2 text-sm font-medium text-white shadow-sm ' +
              'bg-indigo-600 hover:bg-indigo-700 focus-visible:outline focus-visible:outline-2 ' +
              'focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-50'
            }
          >
            {updateMutation.isPending ? 'Saving…' : 'Save Field'}
          </button>
          <Link
            to={`/entities/${entityId}/fields/${fieldId}`}
            className={
              'inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm ' +
              'font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline ' +
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600'
            }
          >
            Cancel
          </Link>
        </div>
      </div>

      {/* ─── Form ─── */}
      <DynamicForm
        id="field-manage-form"
        validation={validation}
        onSubmit={handleSubmit}
      >
        {/* ═══ General Section ═══ */}
        <fieldset className="mb-8 rounded-lg border border-gray-200 p-6">
          <legend className="px-2 text-base font-semibold text-gray-900">General</legend>

          {/* Name (Read Only) */}
          <div className={fieldGroupCls}>
            <label className={labelCls}>Name</label>
            <input type="text" value={name} readOnly className={readOnlyCls} />
          </div>

          {/* Field Id (Display only) */}
          <div className={fieldGroupCls}>
            <label className={labelCls}>Field Id</label>
            <input type="text" value={field.id ?? ''} readOnly className={readOnlyCls} />
          </div>

          {/* Label */}
          <div className={fieldGroupCls}>
            <label className={labelCls} htmlFor="fm-label">Label</label>
            <input
              id="fm-label"
              type="text"
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              className={inputCls}
            />
          </div>

          {/* Required */}
          <div className={fieldGroupCls + ' flex items-center gap-2'}>
            <input
              id="fm-required"
              type="checkbox"
              checked={required}
              onChange={(e) => setRequired(e.target.checked)}
              className={checkboxCls}
            />
            <label htmlFor="fm-required" className="text-sm text-gray-700">Required</label>
          </div>

          {/* Unique */}
          <div className={fieldGroupCls + ' flex items-center gap-2'}>
            <input
              id="fm-unique"
              type="checkbox"
              checked={unique}
              onChange={(e) => setUnique(e.target.checked)}
              className={checkboxCls}
            />
            <label htmlFor="fm-unique" className="text-sm text-gray-700">Unique</label>
          </div>

          {/* Searchable */}
          <div className={fieldGroupCls + ' flex items-center gap-2'}>
            <input
              id="fm-searchable"
              type="checkbox"
              checked={searchable}
              onChange={(e) => setSearchable(e.target.checked)}
              className={checkboxCls}
            />
            <label htmlFor="fm-searchable" className="text-sm text-gray-700">Searchable</label>
          </div>

          {/* System */}
          <div className={fieldGroupCls + ' flex items-center gap-2'}>
            <input
              id="fm-system"
              type="checkbox"
              checked={system}
              onChange={(e) => setSystem(e.target.checked)}
              className={checkboxCls}
            />
            <label htmlFor="fm-system" className="text-sm text-gray-700">System</label>
          </div>

          {/* Description */}
          <div className={fieldGroupCls}>
            <label className={labelCls} htmlFor="fm-description">Description</label>
            <textarea
              id="fm-description"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              rows={3}
              className={inputCls}
            />
          </div>

          {/* Help Text */}
          <div className={fieldGroupCls}>
            <label className={labelCls} htmlFor="fm-helptext">Help Text</label>
            <input
              id="fm-helptext"
              type="text"
              value={helpText}
              onChange={(e) => setHelpText(e.target.value)}
              className={inputCls}
            />
          </div>

          {/* Placeholder Text */}
          <div className={fieldGroupCls}>
            <label className={labelCls} htmlFor="fm-placeholder">Placeholder Text</label>
            <input
              id="fm-placeholder"
              type="text"
              value={placeholderText}
              onChange={(e) => setPlaceholderText(e.target.value)}
              className={inputCls}
            />
          </div>
        </fieldset>

        {/* ═══ Type-Specific Configuration ═══ */}
        <fieldset className="mb-8 rounded-lg border border-gray-200 p-6">
          <legend className="px-2 text-base font-semibold text-gray-900">
            {getFieldTypeName(field.fieldType)} Configuration
          </legend>

          {/* ── AutoNumber ── */}
          {field.fieldType === FieldType.AutoNumberField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls}>Default Value</label>
                <input type="text" value={defaultValue} readOnly className={readOnlyCls} />
                <p className="mt-1 text-xs text-gray-400">Read only — set at creation.</p>
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-displayformat">Display Format</label>
                <input
                  id="fm-displayformat"
                  type="text"
                  value={displayFormat}
                  onChange={(e) => setDisplayFormat(e.target.value)}
                  placeholder="{0}"
                  className={inputCls}
                />
                <p className="mt-1 text-xs text-gray-400">
                  Use &#123;0&#125; as placeholder. E.g. TASK-&#123;0&#125;
                </p>
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls}>Starting Number</label>
                <input type="text" value={startingNumber} readOnly className={readOnlyCls} />
                <p className="mt-1 text-xs text-gray-400">Read only — set at creation.</p>
              </div>
            </>
          )}

          {/* ── Checkbox ── */}
          {field.fieldType === FieldType.CheckboxField && (
            <div className={fieldGroupCls + ' flex items-center gap-2'}>
              <input
                id="fm-checkbox-default"
                type="checkbox"
                checked={defaultValue === 'true'}
                onChange={(e) => setDefaultValue(String(e.target.checked))}
                className={checkboxCls}
              />
              <label htmlFor="fm-checkbox-default" className="text-sm text-gray-700">
                Default Value (checked)
              </label>
            </div>
          )}

          {/* ── Currency ── */}
          {field.fieldType === FieldType.CurrencyField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-currency-default">Default Value</label>
                <input
                  id="fm-currency-default"
                  type="number"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-currency-min">Min Value</label>
                <input
                  id="fm-currency-min"
                  type="number"
                  value={minValue}
                  onChange={(e) => setMinValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-currency-max">Max Value</label>
                <input
                  id="fm-currency-max"
                  type="number"
                  value={maxValue}
                  onChange={(e) => setMaxValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-currency-code">Currency</label>
                <select
                  id="fm-currency-code"
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
          )}

          {/* ── Date / DateTime ── */}
          {(field.fieldType === FieldType.DateField ||
            field.fieldType === FieldType.DateTimeField) && (
            <>
              <div className={fieldGroupCls + ' flex items-center gap-2'}>
                <input
                  id="fm-use-current-time"
                  type="checkbox"
                  checked={useCurrentTimeAsDefaultValue}
                  onChange={(e) => setUseCurrentTimeAsDefaultValue(e.target.checked)}
                  className={checkboxCls}
                />
                <label htmlFor="fm-use-current-time" className="text-sm text-gray-700">
                  Use current time as default value
                </label>
              </div>
              {!useCurrentTimeAsDefaultValue && (
                <div className={fieldGroupCls}>
                  <label className={labelCls} htmlFor="fm-date-default">Default Value</label>
                  <input
                    id="fm-date-default"
                    type="text"
                    value={defaultValue}
                    onChange={(e) => setDefaultValue(e.target.value)}
                    placeholder="e.g. 2024-01-01"
                    className={inputCls}
                  />
                </div>
              )}
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-date-format">Format</label>
                <input
                  id="fm-date-format"
                  type="text"
                  value={dateFormat}
                  onChange={(e) => setDateFormat(e.target.value)}
                  className={inputCls}
                />
              </div>
            </>
          )}

          {/* ── Email ── */}
          {field.fieldType === FieldType.EmailField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-email-default">Default Value</label>
                <input
                  id="fm-email-default"
                  type="text"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-email-maxlen">Max Length</label>
                <input
                  id="fm-email-maxlen"
                  type="number"
                  value={maxLength}
                  onChange={(e) => setMaxLength(e.target.value)}
                  className={inputCls}
                />
              </div>
            </>
          )}

          {/* ── File ── */}
          {field.fieldType === FieldType.FileField && (
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fm-file-default">Default Value</label>
              <input
                id="fm-file-default"
                type="text"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
              />
            </div>
          )}

          {/* ── HTML ── */}
          {field.fieldType === FieldType.HtmlField && (
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fm-html-default">Default Value</label>
              <textarea
                id="fm-html-default"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                rows={5}
                className={inputCls}
              />
            </div>
          )}

          {/* ── Image ── */}
          {field.fieldType === FieldType.ImageField && (
            <div className={fieldGroupCls}>
              <label className={labelCls} htmlFor="fm-image-default">Default Value</label>
              <input
                id="fm-image-default"
                type="text"
                value={defaultValue}
                onChange={(e) => setDefaultValue(e.target.value)}
                className={inputCls}
              />
            </div>
          )}

          {/* ── Multiselect ── */}
          {field.fieldType === FieldType.MultiSelectField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-ms-options">
                  Options <span className="font-normal text-gray-400">(one per line: value,label,iconClass,color)</span>
                </label>
                <textarea
                  id="fm-ms-options"
                  value={optionsText}
                  onChange={(e) => setOptionsText(e.target.value)}
                  rows={8}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-ms-default">
                  Default Values <span className="font-normal text-gray-400">(one value per line)</span>
                </label>
                <textarea
                  id="fm-ms-default"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  rows={4}
                  className={inputCls}
                />
              </div>
            </>
          )}

          {/* ── Number / Percent ── */}
          {(field.fieldType === FieldType.NumberField ||
            field.fieldType === FieldType.PercentField) && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-num-default">Default Value</label>
                <input
                  id="fm-num-default"
                  type="number"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-num-min">Min Value</label>
                <input
                  id="fm-num-min"
                  type="number"
                  value={minValue}
                  onChange={(e) => setMinValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-num-max">Max Value</label>
                <input
                  id="fm-num-max"
                  type="number"
                  value={maxValue}
                  onChange={(e) => setMaxValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-num-decimal">Decimal Places</label>
                <input
                  id="fm-num-decimal"
                  type="number"
                  value={decimalPlaces}
                  onChange={(e) => setDecimalPlaces(e.target.value)}
                  min="0"
                  max="10"
                  className={inputCls}
                />
              </div>
            </>
          )}

          {/* ── Password ── */}
          {field.fieldType === FieldType.PasswordField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-pw-minlen">Min Length</label>
                <input
                  id="fm-pw-minlen"
                  type="number"
                  value={minLength}
                  onChange={(e) => setMinLength(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-pw-maxlen">Max Length</label>
                <input
                  id="fm-pw-maxlen"
                  type="number"
                  value={maxLength}
                  onChange={(e) => setMaxLength(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls + ' flex items-center gap-2'}>
                <input
                  id="fm-pw-encrypted"
                  type="checkbox"
                  checked={encrypted}
                  onChange={(e) => setEncrypted(e.target.checked)}
                  className={checkboxCls}
                />
                <label htmlFor="fm-pw-encrypted" className="text-sm text-gray-700">Encrypted</label>
              </div>
            </>
          )}

          {/* ── Phone ── */}
          {field.fieldType === FieldType.PhoneField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-phone-default">Default Value</label>
                <input
                  id="fm-phone-default"
                  type="text"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-phone-maxlen">Max Length</label>
                <input
                  id="fm-phone-maxlen"
                  type="number"
                  value={maxLength}
                  onChange={(e) => setMaxLength(e.target.value)}
                  className={inputCls}
                />
              </div>
            </>
          )}

          {/* ── GUID ── */}
          {field.fieldType === FieldType.GuidField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-guid-default">Default Value</label>
                <input
                  id="fm-guid-default"
                  type="text"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls + ' flex items-center gap-2'}>
                <input
                  id="fm-guid-generate"
                  type="checkbox"
                  checked={generateNewId}
                  onChange={(e) => setGenerateNewId(e.target.checked)}
                  className={checkboxCls}
                />
                <label htmlFor="fm-guid-generate" className="text-sm text-gray-700">
                  Generate New ID on Create
                </label>
              </div>
            </>
          )}

          {/* ── Select ── */}
          {field.fieldType === FieldType.SelectField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-sel-options">
                  Options <span className="font-normal text-gray-400">(one per line: value,label,iconClass,color)</span>
                </label>
                <textarea
                  id="fm-sel-options"
                  value={optionsText}
                  onChange={(e) => setOptionsText(e.target.value)}
                  rows={8}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-sel-default">Default Value</label>
                <input
                  id="fm-sel-default"
                  type="text"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  className={inputCls}
                />
              </div>
            </>
          )}

          {/* ── Textarea (MultiLineText) ── */}
          {field.fieldType === FieldType.MultiLineTextField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-ta-default">Default Value</label>
                <textarea
                  id="fm-ta-default"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  rows={5}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-ta-maxlen">Max Length</label>
                <input
                  id="fm-ta-maxlen"
                  type="number"
                  value={maxLength}
                  onChange={(e) => setMaxLength(e.target.value)}
                  className={inputCls}
                />
              </div>
            </>
          )}

          {/* ── Text ── */}
          {field.fieldType === FieldType.TextField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-text-default">Default Value</label>
                <input
                  id="fm-text-default"
                  type="text"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-text-maxlen">Max Length</label>
                <input
                  id="fm-text-maxlen"
                  type="number"
                  value={maxLength}
                  onChange={(e) => setMaxLength(e.target.value)}
                  className={inputCls}
                />
              </div>
            </>
          )}

          {/* ── URL ── */}
          {field.fieldType === FieldType.UrlField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-url-default">Default Value</label>
                <input
                  id="fm-url-default"
                  type="text"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-url-maxlen">Max Length</label>
                <input
                  id="fm-url-maxlen"
                  type="number"
                  value={maxLength}
                  onChange={(e) => setMaxLength(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls + ' flex items-center gap-2'}>
                <input
                  id="fm-url-newtab"
                  type="checkbox"
                  checked={openTargetInNewWindow}
                  onChange={(e) => setOpenTargetInNewWindow(e.target.checked)}
                  className={checkboxCls}
                />
                <label htmlFor="fm-url-newtab" className="text-sm text-gray-700">
                  Open in new window/tab
                </label>
              </div>
            </>
          )}

          {/* ── Geography ── */}
          {field.fieldType === FieldType.GeographyField && (
            <>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-geo-default">Default Value</label>
                <textarea
                  id="fm-geo-default"
                  value={defaultValue}
                  onChange={(e) => setDefaultValue(e.target.value)}
                  rows={3}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-geo-maxlen">Max Length</label>
                <input
                  id="fm-geo-maxlen"
                  type="number"
                  value={maxLength}
                  onChange={(e) => setMaxLength(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-geo-srid">SRID</label>
                <input
                  id="fm-geo-srid"
                  type="number"
                  value={geographySrid}
                  onChange={(e) => setGeographySrid(e.target.value)}
                  className={inputCls}
                />
              </div>
              <div className={fieldGroupCls}>
                <label className={labelCls} htmlFor="fm-geo-format">Format</label>
                <select
                  id="fm-geo-format"
                  value={geographyFormat}
                  onChange={(e) => setGeographyFormat(e.target.value)}
                  className={inputCls}
                >
                  <option value={String(GeographyFieldFormat.GeoJSON)}>GeoJSON</option>
                  <option value={String(GeographyFieldFormat.Text)}>Text</option>
                </select>
              </div>
            </>
          )}
        </fieldset>

        {/* ═══ API Security Section ═══ */}
        <fieldset className="mb-8 rounded-lg border border-gray-200 p-6">
          <legend className="px-2 text-base font-semibold text-gray-900">API Security</legend>

          <div className={fieldGroupCls + ' flex items-center gap-2'}>
            <input
              id="fm-enable-security"
              type="checkbox"
              checked={enableSecurity}
              onChange={(e) => setEnableSecurity(e.target.checked)}
              className={checkboxCls}
            />
            <label htmlFor="fm-enable-security" className="text-sm text-gray-700">
              Enable field-level security
            </label>
          </div>

          {enableSecurity && roles.length > 0 && (
            <div className="mt-4 overflow-x-auto">
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
                        <td className="px-4 py-2 text-gray-800">
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
                            aria-label={`Read permission for ${role.name}`}
                          />
                        </td>
                        <td className="px-4 py-2 text-center">
                          <input
                            type="checkbox"
                            checked={rolePerms.includes('update')}
                            onChange={() => togglePermission(role.id, 'update')}
                            className={checkboxCls}
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

          {enableSecurity && roles.length === 0 && (
            <p className="mt-2 text-sm text-gray-400">No roles found. Cannot configure permissions.</p>
          )}
        </fieldset>
      </DynamicForm>
    </section>
  );
}
