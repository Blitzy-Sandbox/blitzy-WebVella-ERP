/**
 * AdminEntityDataManage — Entity Record Edit Page
 *
 * Route: /admin/entities/:entityId/data/:recordId/manage
 *
 * Replaces the monolith's WebVella.Erp.Plugins.SDK/Pages/entity/
 * data-manage.cshtml + data-manage.cshtml.cs.
 *
 * Renders a dynamic form pre-populated with the current record values
 * for editing an existing entity record. Fields are sorted with `id`
 * first, then alphabetically by name — matching the monolith's
 * PageInit() field ordering. The `id` field and AutoNumber-type fields
 * are rendered as read-only display values with hidden inputs. Password
 * fields with empty values are stripped from the update payload so
 * existing passwords are preserved server-side.
 *
 * Uses:
 * - useEntity(entityId) — loads entity schema (metadata, fields, permissions)
 * - useRecord(entityName, recordId) — fetches the existing record data
 * - useUpdateRecord() — TanStack Query mutation for PUT updates
 * - DynamicForm — wrapper providing stacked labels / form mode via context
 */

import { useState, useMemo, useCallback, useEffect } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';

import { useEntity } from '../../hooks/useEntities';
import { useRecord, useUpdateRecord } from '../../hooks/useRecords';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import { FieldType } from '../../types/entity';
import type { Entity, Field, AnyField } from '../../types/entity';

/* -----------------------------------------------------------------------
 * Constants
 * --------------------------------------------------------------------- */

/** Sub-navigation tabs for the entity admin section. */
const ENTITY_SUB_NAV_TABS = [
  { key: 'details', label: 'Details', path: '' },
  { key: 'fields', label: 'Fields', path: '/fields' },
  { key: 'relations', label: 'Relations', path: '/relations' },
  { key: 'data', label: 'Data', path: '/data' },
  { key: 'pages', label: 'Pages', path: '/pages' },
  { key: 'web-api', label: 'Web API', path: '/web-api' },
] as const;

/** Shared Tailwind classes for text-style form inputs. */
const INPUT_CLASSES =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm ' +
  'placeholder:text-gray-400 ' +
  'focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ' +
  'disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500';

/** Tailwind classes for read-only display values. */
const READONLY_CLASSES =
  'block w-full rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-600';

/* -----------------------------------------------------------------------
 * Field Access Helper
 *
 * Mirrors data-manage.cshtml.cs GetFieldAccess():
 * - `id` field → always read-only
 * - AutoNumberField → always read-only
 * - enableSecurity=false → full access
 * - enableSecurity=true → full (server enforces role-based permissions)
 * --------------------------------------------------------------------- */

type FieldAccess = 'full' | 'readonly' | 'forbidden';

function getFieldAccess(field: Field): FieldAccess {
  if (field.name === 'id') return 'readonly';
  if (field.fieldType === FieldType.AutoNumberField) return 'readonly';
  if (!field.enableSecurity) return 'full';
  return 'full';
}

/* -----------------------------------------------------------------------
 * Value Conversion Utilities
 * --------------------------------------------------------------------- */

/** Safely coerce any value to a string for text inputs. */
function toStr(value: unknown): string {
  if (value === null || value === undefined) return '';
  return String(value);
}

/** Safely coerce a value to a numeric string for number inputs. */
function toNumStr(value: unknown): string {
  if (value === null || value === undefined || value === '') return '';
  const n = Number(value);
  return Number.isNaN(n) ? '' : String(n);
}

/** Convert an ISO date/time string to YYYY-MM-DD for date inputs. */
function toDateInput(value: unknown): string {
  if (!value) return '';
  const s = String(value);
  return s.length >= 10 ? s.substring(0, 10) : s;
}

/** Convert an ISO date/time string to YYYY-MM-DDTHH:mm for datetime-local. */
function toDateTimeInput(value: unknown): string {
  if (!value) return '';
  const s = String(value);
  if (s.includes('T')) return s.substring(0, 16);
  return s.length >= 10 ? `${s.substring(0, 10)}T00:00` : s;
}

/* -----------------------------------------------------------------------
 * Field Input Renderer
 *
 * Renders the appropriate HTML input for each of the 21 FieldType values
 * (20 concrete types + AutoNumber displayed read-only). Mirrors the
 * large switch statement in data-manage.cshtml.
 *
 * Uses the AnyField discriminated union so that type narrowing via
 * field.fieldType gives access to type-specific properties such as
 * maxLength, options, currency, decimalPlaces, etc.
 * --------------------------------------------------------------------- */

function renderFieldInput(
  field: AnyField,
  value: unknown,
  onChange: (name: string, val: unknown) => void,
): React.ReactNode {
  const change = (v: unknown) => onChange(field.name, v);

  switch (field.fieldType) {
    /* ── AutoNumber (always read-only — handled by caller, but
         included for exhaustiveness) ── */
    case FieldType.AutoNumberField:
      return <span className={READONLY_CLASSES}>{toStr(value)}</span>;

    /* ── Checkbox ── */
    case FieldType.CheckboxField:
      return (
        <input
          type="checkbox"
          id={`field-${field.name}`}
          name={field.name}
          checked={Boolean(value)}
          onChange={(e) => change(e.target.checked)}
          className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-2 focus:ring-blue-500"
        />
      );

    /* ── Currency ── */
    case FieldType.CurrencyField: {
      const step =
        field.currency?.decimalDigits != null
          ? Math.pow(10, -field.currency.decimalDigits)
          : 0.01;
      return (
        <div className="relative">
          {field.currency?.symbol && (
            <span className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3 text-sm text-gray-500">
              {field.currency.symbol}
            </span>
          )}
          <input
            type="number"
            id={`field-${field.name}`}
            name={field.name}
            value={toNumStr(value)}
            min={field.minValue ?? undefined}
            max={field.maxValue ?? undefined}
            step={step}
            onChange={(e) =>
              change(e.target.value !== '' ? parseFloat(e.target.value) : null)
            }
            placeholder={field.placeholderText || undefined}
            className={`${INPUT_CLASSES}${field.currency?.symbol ? ' ps-7' : ''}`}
          />
        </div>
      );
    }

    /* ── Date ── */
    case FieldType.DateField:
      return (
        <input
          type="date"
          id={`field-${field.name}`}
          name={field.name}
          value={toDateInput(value)}
          onChange={(e) => change(e.target.value || null)}
          placeholder={field.placeholderText || undefined}
          className={INPUT_CLASSES}
        />
      );

    /* ── DateTime ── */
    case FieldType.DateTimeField:
      return (
        <input
          type="datetime-local"
          id={`field-${field.name}`}
          name={field.name}
          value={toDateTimeInput(value)}
          onChange={(e) => change(e.target.value || null)}
          placeholder={field.placeholderText || undefined}
          className={INPUT_CLASSES}
        />
      );

    /* ── Email ── */
    case FieldType.EmailField:
      return (
        <input
          type="email"
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          maxLength={field.maxLength ?? undefined}
          onChange={(e) => change(e.target.value)}
          placeholder={field.placeholderText || undefined}
          className={INPUT_CLASSES}
        />
      );

    /* ── File ── */
    case FieldType.FileField:
      return (
        <input
          type="text"
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          onChange={(e) => change(e.target.value)}
          placeholder={field.placeholderText || 'File path or URL'}
          className={INPUT_CLASSES}
        />
      );

    /* ── HTML ── */
    case FieldType.HtmlField:
      return (
        <textarea
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          onChange={(e) => change(e.target.value)}
          rows={6}
          placeholder={field.placeholderText || undefined}
          className={INPUT_CLASSES}
        />
      );

    /* ── Image ── */
    case FieldType.ImageField:
      return (
        <input
          type="text"
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          onChange={(e) => change(e.target.value)}
          placeholder={field.placeholderText || 'Image URL'}
          className={INPUT_CLASSES}
        />
      );

    /* ── MultiLineText ── */
    case FieldType.MultiLineTextField:
      return (
        <textarea
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          maxLength={field.maxLength ?? undefined}
          onChange={(e) => change(e.target.value)}
          rows={field.visibleLineNumber ?? 4}
          placeholder={field.placeholderText || undefined}
          className={INPUT_CLASSES}
        />
      );

    /* ── MultiSelect ── */
    case FieldType.MultiSelectField:
      return (
        <select
          id={`field-${field.name}`}
          name={field.name}
          multiple
          value={Array.isArray(value) ? (value as string[]).map(String) : []}
          onChange={(e) => {
            const selected = Array.from(
              e.target.selectedOptions,
              (opt) => opt.value,
            );
            change(selected);
          }}
          className={`${INPUT_CLASSES} min-h-[6rem]`}
        >
          {field.options?.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {opt.label || opt.value}
            </option>
          ))}
        </select>
      );

    /* ── Number ── */
    case FieldType.NumberField: {
      const numStep =
        field.decimalPlaces != null && field.decimalPlaces > 0
          ? Math.pow(10, -field.decimalPlaces)
          : 1;
      return (
        <input
          type="number"
          id={`field-${field.name}`}
          name={field.name}
          value={toNumStr(value)}
          min={field.minValue ?? undefined}
          max={field.maxValue ?? undefined}
          step={numStep}
          onChange={(e) =>
            change(e.target.value !== '' ? parseFloat(e.target.value) : null)
          }
          placeholder={field.placeholderText || undefined}
          className={INPUT_CLASSES}
        />
      );
    }

    /* ── Password ── */
    case FieldType.PasswordField:
      return (
        <input
          type="password"
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          maxLength={field.maxLength ?? undefined}
          minLength={field.minLength ?? undefined}
          onChange={(e) => change(e.target.value)}
          placeholder={
            field.placeholderText || 'Leave empty to keep current password'
          }
          autoComplete="new-password"
          className={INPUT_CLASSES}
        />
      );

    /* ── Percent ── */
    case FieldType.PercentField: {
      const pctStep =
        field.decimalPlaces != null && field.decimalPlaces > 0
          ? Math.pow(10, -field.decimalPlaces)
          : 1;
      return (
        <div className="relative">
          <input
            type="number"
            id={`field-${field.name}`}
            name={field.name}
            value={toNumStr(value)}
            min={field.minValue ?? undefined}
            max={field.maxValue ?? undefined}
            step={pctStep}
            onChange={(e) =>
              change(e.target.value !== '' ? parseFloat(e.target.value) : null)
            }
            placeholder={field.placeholderText || undefined}
            className={`${INPUT_CLASSES} pe-8`}
          />
          <span className="pointer-events-none absolute inset-y-0 end-0 flex items-center pe-3 text-sm text-gray-500">
            %
          </span>
        </div>
      );
    }

    /* ── Phone ── */
    case FieldType.PhoneField:
      return (
        <input
          type="tel"
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          maxLength={field.maxLength ?? undefined}
          onChange={(e) => change(e.target.value)}
          placeholder={field.placeholderText || undefined}
          className={INPUT_CLASSES}
        />
      );

    /* ── GUID ── */
    case FieldType.GuidField:
      return (
        <input
          type="text"
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          onChange={(e) => change(e.target.value)}
          placeholder={
            field.placeholderText ||
            'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'
          }
          className={`${INPUT_CLASSES} font-mono text-xs`}
        />
      );

    /* ── Select ── */
    case FieldType.SelectField:
      return (
        <select
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          onChange={(e) => change(e.target.value)}
          className={INPUT_CLASSES}
        >
          <option value="">-- Select --</option>
          {field.options?.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {opt.label || opt.value}
            </option>
          ))}
        </select>
      );

    /* ── Text ── */
    case FieldType.TextField:
      return (
        <input
          type="text"
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          maxLength={field.maxLength ?? undefined}
          onChange={(e) => change(e.target.value)}
          placeholder={field.placeholderText || undefined}
          className={INPUT_CLASSES}
        />
      );

    /* ── URL ── */
    case FieldType.UrlField:
      return (
        <input
          type="url"
          id={`field-${field.name}`}
          name={field.name}
          value={toStr(value)}
          maxLength={field.maxLength ?? undefined}
          onChange={(e) => change(e.target.value)}
          placeholder={field.placeholderText || 'https://'}
          className={INPUT_CLASSES}
        />
      );

    /* ── Geography ── */
    case FieldType.GeographyField:
      return (
        <textarea
          id={`field-${field.name}`}
          name={field.name}
          value={
            typeof value === 'object' && value !== null
              ? JSON.stringify(value, null, 2)
              : toStr(value)
          }
          onChange={(e) => {
            try {
              change(JSON.parse(e.target.value));
            } catch {
              change(e.target.value);
            }
          }}
          rows={field.visibleLineNumber ?? 3}
          placeholder={field.placeholderText || 'GeoJSON or WKT'}
          className={`${INPUT_CLASSES} font-mono text-xs`}
        />
      );

    /* ── Exhaustive fallback (guards future field types) ── */
    default: {
      const _exhaustive: never = field;
      void _exhaustive;
      return (
        <input
          type="text"
          id={`field-${(field as Field).name}`}
          name={(field as Field).name}
          value={toStr(value)}
          onChange={(e) => onChange((field as Field).name, e.target.value)}
          className={INPUT_CLASSES}
        />
      );
    }
  }
}

/* =======================================================================
 * AdminEntityDataManage — Main Page Component
 * ======================================================================= */

function AdminEntityDataManage(): React.ReactNode {
  /* ── Route params ──────────────────────────────────────────── */
  const { entityId = '', recordId = '' } = useParams<{
    entityId: string;
    recordId: string;
  }>();
  const navigate = useNavigate();

  /* ── Data fetching ─────────────────────────────────────────── */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityIsError,
    error: entityError,
  } = useEntity(entityId);

  // Entity name is required to fetch the record; useRecord is disabled
  // until entity data has loaded (entityName is non-empty).
  const entityName = entity?.name ?? '';

  const {
    data: record,
    isLoading: recordLoading,
    isError: recordIsError,
    error: recordError,
  } = useRecord(entityName, recordId);

  const updateMutation = useUpdateRecord();

  /* ── Form state ────────────────────────────────────────────── */
  const [formData, setFormData] = useState<Record<string, unknown>>({});
  const [validation, setValidation] = useState<FormValidation>({ errors: [] });
  const [loadedRecordId, setLoadedRecordId] = useState<string>('');

  /* ── Pre-populate form when record loads / changes ─────────── */
  useEffect(() => {
    if (record && recordId && recordId !== loadedRecordId) {
      const initial: Record<string, unknown> = {};
      for (const key of Object.keys(record)) {
        initial[key] = record[key];
      }
      // Clear password fields so they appear empty in the edit form.
      // The server preserves existing passwords when empty values are omitted.
      if (entity?.fields) {
        for (const f of entity.fields) {
          if (f.fieldType === FieldType.PasswordField) {
            initial[f.name] = '';
          }
        }
      }
      setFormData(initial);
      setLoadedRecordId(recordId);
      setValidation({ errors: [] });
    }
  }, [record, recordId, loadedRecordId, entity?.fields]);

  /* ── Sort fields: id first, then alphabetical by name ──────── */
  const sortedFields = useMemo((): AnyField[] => {
    if (!entity?.fields) return [];
    return ([...entity.fields] as AnyField[]).sort(
      (a: Field, b: Field) => {
        if (a.name === 'id') return -1;
        if (b.name === 'id') return 1;
        return a.name.localeCompare(b.name);
      },
    );
  }, [entity?.fields]);

  /* ── Filter out Forbidden-access fields ────────────────────── */
  const visibleFields = useMemo(
    () => sortedFields.filter((f) => getFieldAccess(f) !== 'forbidden'),
    [sortedFields],
  );

  /* ── Field change handler ──────────────────────────────────── */
  const handleFieldChange = useCallback(
    (fieldName: string, value: unknown) => {
      setFormData((prev) => ({ ...prev, [fieldName]: value }));
    },
    [],
  );

  /* ── Form submit handler ───────────────────────────────────── */
  const handleSubmit = useCallback(() => {
    if (!entityName || !recordId) return;

    setValidation({ errors: [] });

    // Build the update payload.
    // Mirrors data-manage.cshtml.cs OnPost(): strip empty password fields
    // so the server preserves existing password values.
    const payload: Record<string, unknown> = {};
    for (const field of visibleFields) {
      const val = formData[field.name];

      if (field.fieldType === FieldType.PasswordField) {
        // Only send password value when the user entered a new one.
        const strVal = toStr(val);
        if (strVal.length > 0) {
          payload[field.name] = strVal;
        }
      } else {
        payload[field.name] = val;
      }
    }

    // Always include the record id in the payload.
    if (!payload.id && recordId) {
      payload.id = recordId;
    }

    updateMutation.mutate(
      { entityName, id: recordId, data: payload },
      {
        onSuccess: () => {
          navigate(`/admin/entities/${entityId}/data`);
        },
        onError: (err: Error) => {
          const message =
            err.message || 'An error occurred while updating the record.';

          // Attempt to split concatenated validation messages into
          // individual error items. The assertApiSuccess helper joins
          // multiple server errors with '; '.
          const errors: ValidationError[] = [];
          if (err.message?.includes(';')) {
            for (const part of err.message.split(';')) {
              const trimmed = part.trim();
              if (trimmed) {
                errors.push({ propertyName: '', message: trimmed });
              }
            }
          }

          setValidation({ message, errors });
          window.scrollTo({ top: 0, behavior: 'smooth' });
        },
      },
    );
  }, [
    entityName,
    recordId,
    entityId,
    formData,
    visibleFields,
    updateMutation,
    navigate,
  ]);

  /* ── Computed helpers ───────────────────────────────────────── */
  const isLoading = entityLoading || recordLoading;
  const typedEntity: Entity | undefined = entity ?? undefined;

  /* =================================================================
   * Render — Loading State
   * ================================================================= */
  if (isLoading) {
    return (
      <div className="flex min-h-[24rem] items-center justify-center">
        <div className="text-center">
          <div
            className="mx-auto mb-4 h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-t-transparent"
            role="status"
            aria-label="Loading"
          />
          <p className="text-sm text-gray-500">Loading record…</p>
        </div>
      </div>
    );
  }

  /* =================================================================
   * Render — Error State
   * ================================================================= */
  if (entityIsError || recordIsError) {
    const errorMsg =
      (entityError as Error | null)?.message ??
      (recordError as Error | null)?.message ??
      'Failed to load data.';
    return (
      <div className="mx-auto max-w-3xl px-4 py-8">
        <div
          role="alert"
          className="rounded-lg border border-red-200 bg-red-50 p-6 text-center"
        >
          <h2 className="mb-2 text-lg font-semibold text-red-800">
            Error Loading Record
          </h2>
          <p className="mb-4 text-sm text-red-700">{errorMsg}</p>
          <Link
            to={`/admin/entities/${entityId}/data`}
            className="inline-flex items-center rounded-md bg-red-100 px-4 py-2 text-sm font-medium text-red-800 hover:bg-red-200"
          >
            ← Back to Data
          </Link>
        </div>
      </div>
    );
  }

  /* =================================================================
   * Render — Not-Found State
   * ================================================================= */
  if (!typedEntity || !record) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-8">
        <div className="rounded-lg border border-yellow-200 bg-yellow-50 p-6 text-center">
          <h2 className="mb-2 text-lg font-semibold text-yellow-800">
            Record Not Found
          </h2>
          <p className="mb-4 text-sm text-yellow-700">
            The requested record could not be found.
          </p>
          <Link
            to={`/admin/entities/${entityId}/data`}
            className="inline-flex items-center rounded-md bg-yellow-100 px-4 py-2 text-sm font-medium text-yellow-800 hover:bg-yellow-200"
          >
            ← Back to Data
          </Link>
        </div>
      </div>
    );
  }

  /* =================================================================
   * Render — Main Page
   * ================================================================= */
  return (
    <div className="min-h-screen bg-gray-50">
      {/* ── Page Header ── */}
      <header className="border-b border-gray-200 bg-white px-6 py-4">
        {/* Breadcrumb */}
        <nav aria-label="Breadcrumb" className="mb-2">
          <ol className="flex items-center gap-1 text-sm text-gray-500">
            <li>
              <Link to="/admin" className="hover:text-gray-700">
                Admin
              </Link>
            </li>
            <li aria-hidden="true" className="px-1">
              /
            </li>
            <li>
              <Link to="/admin/entities" className="hover:text-gray-700">
                Entities
              </Link>
            </li>
            <li aria-hidden="true" className="px-1">
              /
            </li>
            <li>
              <Link
                to={`/admin/entities/${entityId}`}
                className="hover:text-gray-700"
              >
                {typedEntity.label || typedEntity.name}
              </Link>
            </li>
            <li aria-hidden="true" className="px-1">
              /
            </li>
            <li>
              <Link
                to={`/admin/entities/${entityId}/data`}
                className="hover:text-gray-700"
              >
                Data
              </Link>
            </li>
            <li aria-hidden="true" className="px-1">
              /
            </li>
            <li className="font-medium text-gray-900" aria-current="page">
              Edit Record
            </li>
          </ol>
        </nav>

        {/* Entity title with colour accent */}
        <div className="flex items-center gap-3">
          {typedEntity.iconName && (
            <span
              className={`${typedEntity.iconName} text-xl`}
              style={{ color: typedEntity.color || undefined }}
              aria-hidden="true"
            />
          )}
          <h1 className="text-xl font-semibold text-gray-900">
            <span style={{ color: typedEntity.color || undefined }}>
              {typedEntity.label || typedEntity.name}
            </span>
            {' — '}
            <span className="font-normal text-gray-600">Edit Record</span>
          </h1>
        </div>
      </header>

      {/* ── Sub-Navigation Tabs ── */}
      <nav
        className="border-b border-gray-200 bg-white px-6"
        aria-label="Entity sections"
      >
        <ul className="flex gap-0" role="tablist">
          {ENTITY_SUB_NAV_TABS.map((tab) => {
            const isActive = tab.key === 'data';
            return (
              <li key={tab.key} role="presentation">
                <Link
                  to={`/admin/entities/${entityId}${tab.path}`}
                  role="tab"
                  aria-selected={isActive}
                  className={`inline-block border-b-2 px-4 py-3 text-sm font-medium transition-colors ${
                    isActive
                      ? 'border-blue-500 text-blue-600'
                      : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700'
                  }`}
                >
                  {tab.label}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>

      {/* ── Main Content ── */}
      <main className="mx-auto max-w-3xl px-6 py-6">
        <DynamicForm
          labelMode="stacked"
          fieldMode="form"
          validation={validation}
          onSubmit={handleSubmit}
          name="entity-data-manage"
        >
          {/* Field List */}
          <div className="space-y-5">
            {visibleFields.map((field) => {
              const access = getFieldAccess(field);
              const value = formData[field.name];

              return (
                <div key={field.id} className="group">
                  {/* Label */}
                  <label
                    htmlFor={`field-${field.name}`}
                    className="mb-1 block text-sm font-medium text-gray-700"
                  >
                    {field.label || field.name}
                    {field.required && (
                      <span
                        className="ms-0.5 text-red-500"
                        aria-label="required"
                      >
                        *
                      </span>
                    )}
                  </label>

                  {/* Help text above the input */}
                  {field.helpText && (
                    <p
                      id={`help-${field.name}`}
                      className="mb-1 text-xs text-gray-500"
                    >
                      {field.helpText}
                    </p>
                  )}

                  {/* Read-only display or editable input */}
                  {access === 'readonly' ? (
                    <>
                      <span className={READONLY_CLASSES}>
                        {toStr(value)}
                      </span>
                      <input
                        type="hidden"
                        name={field.name}
                        value={toStr(value)}
                      />
                    </>
                  ) : (
                    renderFieldInput(field, value, handleFieldChange)
                  )}

                  {/* Description below the input */}
                  {field.description && (
                    <p className="mt-1 text-xs text-gray-400">
                      {field.description}
                    </p>
                  )}
                </div>
              );
            })}
          </div>

          {/* Form Actions */}
          <div className="mt-8 flex items-center gap-3 border-t border-gray-200 pt-6">
            <button
              type="submit"
              disabled={updateMutation.isPending}
              className={
                'inline-flex items-center rounded-md px-4 py-2 text-sm font-medium text-white shadow-sm ' +
                'bg-blue-600 hover:bg-blue-700 ' +
                'focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 ' +
                'disabled:cursor-not-allowed disabled:opacity-50'
              }
            >
              {updateMutation.isPending ? (
                <>
                  <span
                    className="me-2 inline-block h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                    aria-hidden="true"
                  />
                  Saving…
                </>
              ) : (
                'Save'
              )}
            </button>

            <Link
              to={`/admin/entities/${entityId}/data`}
              className={
                'inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm ' +
                'font-medium text-gray-700 shadow-sm hover:bg-gray-50 ' +
                'focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2'
              }
            >
              Cancel
            </Link>
          </div>
        </DynamicForm>
      </main>
    </div>
  );
}

export default AdminEntityDataManage;
