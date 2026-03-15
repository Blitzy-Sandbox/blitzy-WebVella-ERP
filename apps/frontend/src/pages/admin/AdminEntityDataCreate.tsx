/**
 * AdminEntityDataCreate — Entity Record Creation Page
 *
 * React page component that replaces the monolith's
 * `WebVella.Erp.Plugins.SDK/Pages/entity/data-create.cshtml[.cs]`.
 *
 * Dynamically generates a record creation form based on the entity's field
 * definitions, supporting all 20+ field types. Fields are sorted with the
 * `id` field first, then alphabetically by name — matching the monolith's
 * `PageInit()` sorting logic.
 *
 * Route: `/admin/entities/:entityId/data/create`
 *
 * Key behaviours ported from the C# source:
 *  - Entity metadata fetched via `useEntity(entityId)` — replaces
 *    `EntityManager.ReadEntity(ParentRecordId)`.
 *  - Field access filtering: fields with Forbidden access are excluded
 *    from the form (admin context defaults to Full access).
 *  - Password empty-value cleanup: empty password fields are stripped
 *    from the record payload before submission — matches OnPost() logic.
 *  - Auto-generated GUID: when the `id` field is null/empty a new GUID
 *    is generated via `crypto.randomUUID()`.
 *  - Server-side validation errors displayed via DynamicForm's
 *    `FormValidation` state.
 *  - On success navigates to the entity data list page.
 *
 * @module pages/admin/AdminEntityDataCreate
 */

import { useState, useMemo, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useEntity } from '../../hooks/useEntities';
import { useCreateRecord } from '../../hooks/useRecords';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import { FieldType } from '../../types/entity';
import type { Entity, AnyField, Field } from '../../types/entity';

// ---------------------------------------------------------------------------
// Field Access
// ---------------------------------------------------------------------------

/**
 * Possible access levels for an entity field.
 * Maps to the monolith's `FieldAccess` enum in data-create.cshtml.cs.
 */
type FieldAccess = 'full' | 'readOnly' | 'forbidden';

/**
 * Determines the access level for a single entity field.
 *
 * Maps to `data-create.cshtml.cs` → `GetFieldAccess(Field field)` which:
 *  1. Returns Full when `enableSecurity` is false.
 *  2. Returns Full when the current user's roles intersect `canUpdate`.
 *  3. Returns ReadOnly when the current user's roles intersect `canRead`.
 *  4. Returns Forbidden otherwise.
 *
 * In the admin SDK context, the authenticated user is an administrator who
 * holds all role assignments. Server-side Lambda handlers enforce the
 * actual permission checks; the client therefore defaults to Full for all
 * fields rendered in the admin panel.
 */
function getFieldAccess(field: Field): FieldAccess {
  if (!field.enableSecurity) {
    return 'full';
  }
  // Admin panel: administrators have all role permissions.
  // Actual enforcement happens server-side in the Lambda handler.
  return 'full';
}

// ---------------------------------------------------------------------------
// Field Sorting
// ---------------------------------------------------------------------------

/**
 * Sorts entity fields: `id` first, then remaining fields alphabetically
 * by `name`.
 *
 * Replicates data-create.cshtml.cs `PageInit()` lines 29-30:
 * ```csharp
 * entity.Fields.Sort((f1, f2) =>
 *   f1.Name == "id" ? -1 : f1.Name.CompareTo(f2.Name));
 * ```
 */
function sortFields(fields: ReadonlyArray<Field>): AnyField[] {
  return ([...fields] as AnyField[]).sort((a, b) => {
    if (a.name === 'id') return -1;
    if (b.name === 'id') return 1;
    return a.name.localeCompare(b.name);
  });
}

// ---------------------------------------------------------------------------
// Default Value Computation
// ---------------------------------------------------------------------------

/**
 * Computes the initial form value for a single field based on its type
 * and configured default. Maps to the monolith's per-type default logic
 * spread across each `<wv-field-*>` tag helper's `value` attribute in
 * data-create.cshtml.
 */
function getFieldDefaultValue(field: AnyField): unknown {
  switch (field.fieldType) {
    case FieldType.AutoNumberField:
      // AutoNumber is server-generated; display as null/read-only
      return null;

    case FieldType.CheckboxField:
      return field.defaultValue ?? false;

    case FieldType.CurrencyField:
      return field.defaultValue ?? null;

    case FieldType.DateField:
      if (field.useCurrentTimeAsDefaultValue) {
        return new Date().toISOString().split('T')[0];
      }
      return field.defaultValue ?? '';

    case FieldType.DateTimeField:
      if (field.useCurrentTimeAsDefaultValue) {
        return new Date().toISOString();
      }
      return field.defaultValue ?? '';

    case FieldType.EmailField:
      return field.defaultValue ?? '';

    case FieldType.FileField:
      return field.defaultValue ?? '';

    case FieldType.HtmlField:
      return field.defaultValue ?? '';

    case FieldType.ImageField:
      return field.defaultValue ?? '';

    case FieldType.MultiLineTextField:
      return field.defaultValue ?? '';

    case FieldType.MultiSelectField:
      return field.defaultValue ?? [];

    case FieldType.NumberField:
      return field.defaultValue ?? null;

    case FieldType.PasswordField:
      // Passwords never pre-populate — always empty on create
      return '';

    case FieldType.PercentField:
      return field.defaultValue ?? null;

    case FieldType.PhoneField:
      return field.defaultValue ?? '';

    case FieldType.GuidField:
      if (field.generateNewId) {
        return crypto.randomUUID();
      }
      return field.defaultValue ?? '';

    case FieldType.SelectField:
      return field.defaultValue ?? '';

    case FieldType.TextField:
      return field.defaultValue ?? '';

    case FieldType.UrlField:
      return field.defaultValue ?? '';

    case FieldType.GeographyField:
      return field.defaultValue ?? '';

    default:
      return '';
  }
}

/**
 * Builds the initial form data record populated with default values from
 * every visible field definition.
 */
function buildInitialFormData(fields: ReadonlyArray<AnyField>): Record<string, unknown> {
  const data: Record<string, unknown> = {};
  for (const field of fields) {
    data[field.name] = getFieldDefaultValue(field);
  }
  return data;
}

// ---------------------------------------------------------------------------
// Validation Error Parsing
// ---------------------------------------------------------------------------

/**
 * Attempts to extract structured field-level validation errors from a
 * server error message string.
 *
 * The Entity Management Lambda returns errors via `assertApiSuccess()`
 * which concatenates error messages with `;` separators. Each individual
 * error may contain a `fieldName: message` pair.
 */
function parseValidationErrors(message: string): ValidationError[] {
  const errors: ValidationError[] = [];
  const parts = message
    .split(';')
    .map((s) => s.trim())
    .filter(Boolean);

  for (const part of parts) {
    const colonIdx = part.indexOf(':');
    if (colonIdx > 0) {
      const propertyName = part.substring(0, colonIdx).trim();
      const msg = part.substring(colonIdx + 1).trim();
      if (propertyName && msg) {
        errors.push({ propertyName, message: msg });
      }
    }
  }
  return errors;
}

// ---------------------------------------------------------------------------
// DateTime Helpers
// ---------------------------------------------------------------------------

/**
 * Converts an ISO 8601 datetime string to the `YYYY-MM-DDThh:mm` format
 * expected by `<input type="datetime-local">`.
 */
function formatDateTimeLocal(iso: string): string {
  if (!iso) return '';
  try {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return '';
    // Slice to "YYYY-MM-DDThh:mm"
    return date.toISOString().slice(0, 16);
  } catch {
    return '';
  }
}

// ---------------------------------------------------------------------------
// Shared Tailwind Class Strings
// ---------------------------------------------------------------------------

const INPUT_BASE_CLASSES =
  'block w-full rounded-md border px-3 py-2 text-sm shadow-sm transition-colors ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-500 ' +
  'disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500';

const INPUT_NORMAL = `${INPUT_BASE_CLASSES} border-gray-300 text-gray-900 placeholder:text-gray-400`;
const INPUT_ERROR = `${INPUT_BASE_CLASSES} border-red-300 text-red-900 placeholder:text-red-300`;

function inputClasses(hasError: boolean): string {
  return hasError ? INPUT_ERROR : INPUT_NORMAL;
}

// ---------------------------------------------------------------------------
// AdminEntityDataCreate Component
// ---------------------------------------------------------------------------

/**
 * Entity record creation page rendered at
 * `/admin/entities/:entityId/data/create`.
 *
 * Default export enables React.lazy() route-level code splitting.
 */
export default function AdminEntityDataCreate(): React.ReactNode {
  // ── Route Params & Navigation ───────────────────────────────
  const { entityId = '' } = useParams<{ entityId: string }>();
  const navigate = useNavigate();

  // ── Entity Schema Query ─────────────────────────────────────
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityIsError,
    error: entityError,
  } = useEntity(entityId) as {
    data: Entity | undefined;
    isLoading: boolean;
    isError: boolean;
    error: Error | null;
  };

  // ── Record Creation Mutation ────────────────────────────────
  const {
    mutate,
    mutateAsync,
    isPending,
    isError: mutationIsError,
    error: mutationError,
    isSuccess,
    data: createdRecord,
    reset: resetMutation,
  } = useCreateRecord();

  // ── Form State ──────────────────────────────────────────────
  const [formData, setFormData] = useState<Record<string, unknown>>({});
  const [validation, setValidation] = useState<FormValidation | undefined>(
    undefined,
  );
  // Tracks which entityId the form was last initialized for, so we
  // re-initialise defaults if the route param changes.
  const [initializedForEntity, setInitializedForEntity] = useState('');

  // ── Compute Visible Fields ──────────────────────────────────
  const visibleFields = useMemo<AnyField[]>(() => {
    if (!entity?.fields) return [];
    const sorted = sortFields(entity.fields);
    return sorted.filter((f) => getFieldAccess(f) !== 'forbidden');
  }, [entity?.fields]);

  // ── Initialise Form Data on Entity Load ─────────────────────
  // React-recommended "adjusting state during render" pattern:
  // https://react.dev/learn/you-might-not-need-an-effect#adjusting-some-state-when-a-prop-changes
  if (entity && entity.id !== initializedForEntity && visibleFields.length > 0) {
    setInitializedForEntity(entity.id);
    setFormData(buildInitialFormData(visibleFields));
    setValidation(undefined);
    resetMutation();
  }

  // ── Per-Field Change Handler ────────────────────────────────
  const handleFieldChange = useCallback(
    (fieldName: string, value: unknown) => {
      setFormData((prev) => ({ ...prev, [fieldName]: value }));
      // Clear field-specific validation error on edit
      setValidation((prev) => {
        if (!prev) return prev;
        const remaining = prev.errors.filter(
          (e) => e.propertyName !== fieldName,
        );
        if (remaining.length === prev.errors.length) return prev;
        return { ...prev, errors: remaining };
      });
    },
    [],
  );

  // ── Form Submission ─────────────────────────────────────────
  const handleSubmit = useCallback(async () => {
    if (!entity) return;

    // Build the record payload from current form data
    const record: Record<string, unknown> = { ...formData };

    // Auto-generate GUID for id if null/empty.
    // Matches data-create.cshtml.cs OnPost() lines 43-45:
    //   if (record["id"] == null) record["id"] = Guid.NewGuid();
    if (!record['id']) {
      record['id'] = crypto.randomUUID();
    }

    // Remove empty password fields from the payload.
    // Matches data-create.cshtml.cs OnPost() lines 32-40 where empty
    // password fields are stripped so the server doesn't overwrite
    // existing hashed values with an empty string.
    for (const field of visibleFields) {
      if (field.fieldType === FieldType.PasswordField) {
        const val = record[field.name];
        if (val === '' || val === null || val === undefined) {
          delete record[field.name];
        }
      }
    }

    // Clear previous validation state
    setValidation(undefined);

    try {
      await mutateAsync({
        entityName: entity.name,
        data: record,
      });

      // Success → navigate to entity data list
      navigate(`/admin/entities/${entityId}/data`);
    } catch (err: unknown) {
      const message =
        err instanceof Error ? err.message : 'Record creation failed.';
      const fieldErrors = parseValidationErrors(message);
      setValidation({
        message: fieldErrors.length > 0 ? undefined : message,
        errors: fieldErrors,
      });
    }
  }, [entity, entityId, formData, mutateAsync, navigate, visibleFields]);

  // ── Field Input Renderer ────────────────────────────────────

  /**
   * Renders the appropriate HTML input control for a single entity field
   * based on its `fieldType` discriminator.
   *
   * Maps to the `@switch(field.GetType().Name)` block in
   * data-create.cshtml lines 50-295, where each field type gets a
   * dedicated `<wv-field-*>` tag helper with type-specific attributes.
   */
  const renderFieldInput = useCallback(
    (field: AnyField): React.ReactNode => {
      const access = getFieldAccess(field);
      const isReadOnly = access === 'readOnly';
      const value = formData[field.name];
      const fieldErrors =
        validation?.errors.filter((e) => e.propertyName === field.name) ?? [];
      const hasError = fieldErrors.length > 0;
      const classes = inputClasses(hasError);

      switch (field.fieldType) {
        /* ── AutoNumberField ──────────────────────────────── */
        case FieldType.AutoNumberField:
          return (
            <input
              type="text"
              id={`field-${field.name}`}
              name={field.name}
              value={field.displayFormat || 'Auto-generated'}
              disabled
              className={inputClasses(false)}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
            />
          );

        /* ── TextField ────────────────────────────────────── */
        case FieldType.TextField:
          return (
            <input
              type="text"
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              maxLength={field.maxLength ?? undefined}
              placeholder={field.placeholderText}
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── MultiLineTextField ───────────────────────────── */
        case FieldType.MultiLineTextField:
          return (
            <textarea
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              maxLength={field.maxLength ?? undefined}
              rows={field.visibleLineNumber ?? 4}
              placeholder={field.placeholderText}
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── GeographyField ───────────────────────────────── */
        case FieldType.GeographyField:
          return (
            <textarea
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              maxLength={field.maxLength ?? undefined}
              rows={field.visibleLineNumber ?? 4}
              placeholder={
                field.placeholderText || 'GeoJSON or WKT format'
              }
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── EmailField ───────────────────────────────────── */
        case FieldType.EmailField:
          return (
            <input
              type="email"
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              maxLength={field.maxLength ?? undefined}
              placeholder={field.placeholderText}
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── UrlField ─────────────────────────────────────── */
        case FieldType.UrlField:
          return (
            <input
              type="url"
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              maxLength={field.maxLength ?? undefined}
              placeholder={field.placeholderText}
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── PhoneField ───────────────────────────────────── */
        case FieldType.PhoneField:
          return (
            <input
              type="tel"
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              maxLength={field.maxLength ?? undefined}
              placeholder={field.placeholderText}
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── NumberField ──────────────────────────────────── */
        case FieldType.NumberField:
          return (
            <input
              type="number"
              id={`field-${field.name}`}
              name={field.name}
              value={
                value !== null && value !== undefined ? String(value) : ''
              }
              onChange={(e) =>
                handleFieldChange(
                  field.name,
                  e.target.value === '' ? null : Number(e.target.value),
                )
              }
              min={field.minValue ?? undefined}
              max={field.maxValue ?? undefined}
              step={
                field.decimalPlaces != null
                  ? String(Math.pow(10, -field.decimalPlaces))
                  : undefined
              }
              placeholder={field.placeholderText}
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── CurrencyField ────────────────────────────────── */
        case FieldType.CurrencyField:
          return (
            <div className="relative">
              {field.currency?.symbol && (
                <span
                  className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3 text-sm text-gray-500"
                  aria-hidden="true"
                >
                  {field.currency.symbol}
                </span>
              )}
              <input
                type="number"
                id={`field-${field.name}`}
                name={field.name}
                value={
                  value !== null && value !== undefined ? String(value) : ''
                }
                onChange={(e) =>
                  handleFieldChange(
                    field.name,
                    e.target.value === '' ? null : Number(e.target.value),
                  )
                }
                min={field.minValue ?? undefined}
                max={field.maxValue ?? undefined}
                step={
                  field.currency?.decimalDigits != null
                    ? String(Math.pow(10, -field.currency.decimalDigits))
                    : '0.01'
                }
                placeholder={field.placeholderText}
                required={field.required}
                readOnly={isReadOnly}
                className={`${classes} ${field.currency?.symbol ? 'ps-7' : ''}`}
                aria-describedby={
                  field.helpText ? `help-${field.name}` : undefined
                }
                aria-invalid={hasError || undefined}
              />
            </div>
          );

        /* ── PercentField ─────────────────────────────────── */
        case FieldType.PercentField:
          return (
            <div className="relative">
              <input
                type="number"
                id={`field-${field.name}`}
                name={field.name}
                value={
                  value !== null && value !== undefined ? String(value) : ''
                }
                onChange={(e) =>
                  handleFieldChange(
                    field.name,
                    e.target.value === '' ? null : Number(e.target.value),
                  )
                }
                min={field.minValue ?? undefined}
                max={field.maxValue ?? undefined}
                step={
                  field.decimalPlaces != null
                    ? String(Math.pow(10, -field.decimalPlaces))
                    : undefined
                }
                placeholder={field.placeholderText}
                required={field.required}
                readOnly={isReadOnly}
                className={`${classes} pe-8`}
                aria-describedby={
                  field.helpText ? `help-${field.name}` : undefined
                }
                aria-invalid={hasError || undefined}
              />
              <span
                className="pointer-events-none absolute inset-y-0 end-0 flex items-center pe-3 text-sm text-gray-500"
                aria-hidden="true"
              >
                %
              </span>
            </div>
          );

        /* ── CheckboxField ────────────────────────────────── */
        case FieldType.CheckboxField:
          return (
            <input
              type="checkbox"
              id={`field-${field.name}`}
              name={field.name}
              checked={Boolean(value)}
              onChange={(e) =>
                handleFieldChange(field.name, e.target.checked)
              }
              disabled={isReadOnly}
              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── DateField ────────────────────────────────────── */
        case FieldType.DateField:
          return (
            <input
              type="date"
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── DateTimeField ────────────────────────────────── */
        case FieldType.DateTimeField:
          return (
            <input
              type="datetime-local"
              id={`field-${field.name}`}
              name={field.name}
              value={formatDateTimeLocal((value as string) ?? '')}
              onChange={(e) =>
                handleFieldChange(
                  field.name,
                  e.target.value
                    ? new Date(e.target.value).toISOString()
                    : '',
                )
              }
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── PasswordField ────────────────────────────────── */
        case FieldType.PasswordField:
          return (
            <input
              type="password"
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              minLength={field.minLength ?? undefined}
              maxLength={field.maxLength ?? undefined}
              placeholder={
                field.placeholderText || 'Leave empty to skip'
              }
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              autoComplete="new-password"
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── GuidField ────────────────────────────────────── */
        case FieldType.GuidField:
          return (
            <input
              type="text"
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              placeholder={
                field.placeholderText ||
                'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'
              }
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── SelectField ──────────────────────────────────── */
        case FieldType.SelectField:
          return (
            <select
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              required={field.required}
              disabled={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            >
              <option value="">
                {field.placeholderText || '— Select —'}
              </option>
              {field.options.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          );

        /* ── MultiSelectField ─────────────────────────────── */
        case FieldType.MultiSelectField:
          return (
            <fieldset
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
            >
              <legend className="sr-only">{field.label}</legend>
              <div className="space-y-2 rounded-md border border-gray-200 p-3">
                {field.options.length > 0 ? (
                  field.options.map((opt) => {
                    const checked =
                      Array.isArray(value) &&
                      (value as string[]).includes(opt.value);
                    return (
                      <label
                        key={opt.value}
                        className="flex items-center gap-2 text-sm"
                      >
                        <input
                          type="checkbox"
                          checked={checked}
                          onChange={(e) => {
                            const current = Array.isArray(value)
                              ? (value as string[])
                              : [];
                            const next = e.target.checked
                              ? [...current, opt.value]
                              : current.filter((v) => v !== opt.value);
                            handleFieldChange(field.name, next);
                          }}
                          disabled={isReadOnly}
                          className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
                        />
                        {opt.color && (
                          <span
                            className="inline-block h-3 w-3 rounded-full"
                            style={{ backgroundColor: opt.color }}
                            aria-hidden="true"
                          />
                        )}
                        <span className="text-gray-700">{opt.label}</span>
                      </label>
                    );
                  })
                ) : (
                  <p className="text-sm text-gray-500">
                    No options defined for this field.
                  </p>
                )}
              </div>
            </fieldset>
          );

        /* ── FileField ────────────────────────────────────── */
        case FieldType.FileField:
          return (
            <input
              type="text"
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              placeholder={field.placeholderText || 'File path or URL'}
              required={field.required}
              readOnly={isReadOnly}
              className={classes}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── ImageField ───────────────────────────────────── */
        case FieldType.ImageField:
          return (
            <div className="space-y-2">
              <input
                type="text"
                id={`field-${field.name}`}
                name={field.name}
                value={(value as string) ?? ''}
                onChange={(e) =>
                  handleFieldChange(field.name, e.target.value)
                }
                placeholder={
                  field.placeholderText || 'Image path or URL'
                }
                required={field.required}
                readOnly={isReadOnly}
                className={classes}
                aria-describedby={
                  field.helpText ? `help-${field.name}` : undefined
                }
                aria-invalid={hasError || undefined}
              />
              {typeof value === 'string' && value.length > 0 && (
                <img
                  src={value}
                  alt={`Preview for ${field.label}`}
                  className="mt-1 max-h-32 rounded border border-gray-200 bg-gray-100"
                  width={128}
                  height={128}
                  loading="lazy"
                  decoding="async"
                />
              )}
            </div>
          );

        /* ── HtmlField ────────────────────────────────────── */
        case FieldType.HtmlField:
          return (
            <textarea
              id={`field-${field.name}`}
              name={field.name}
              value={(value as string) ?? ''}
              onChange={(e) => handleFieldChange(field.name, e.target.value)}
              rows={6}
              placeholder={
                field.placeholderText || 'Enter HTML content'
              }
              required={field.required}
              readOnly={isReadOnly}
              className={`${classes} font-mono text-xs`}
              aria-describedby={
                field.helpText ? `help-${field.name}` : undefined
              }
              aria-invalid={hasError || undefined}
            />
          );

        /* ── Fallback for unknown/future field types ─────── */
        /* Covers RelationField and any other types not in     */
        /* the AnyField discriminated union.                    */
        default: {
          const fallbackField = field as Field;
          return (
            <input
              type="text"
              id={`field-${fallbackField.name}`}
              name={fallbackField.name}
              value={String(value ?? '')}
              onChange={(e) =>
                handleFieldChange(fallbackField.name, e.target.value)
              }
              placeholder={fallbackField.placeholderText}
              className={classes}
              aria-describedby={
                fallbackField.helpText
                  ? `help-${fallbackField.name}`
                  : undefined
              }
            />
          );
        }
      }
    },
    [formData, handleFieldChange, validation],
  );

  // ── Loading State ───────────────────────────────────────────
  if (entityLoading) {
    return (
      <div className="flex items-center justify-center p-12" role="status">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-blue-500 border-t-transparent" />
        <span className="sr-only">Loading entity schema…</span>
      </div>
    );
  }

  // ── Error State ─────────────────────────────────────────────
  if (entityIsError) {
    return (
      <div className="mx-auto max-w-3xl p-6" role="alert">
        <div className="rounded-lg border border-red-200 bg-red-50 p-6">
          <h2 className="mb-2 text-lg font-semibold text-red-800">
            Failed to Load Entity
          </h2>
          <p className="text-sm text-red-700">
            {entityError instanceof Error
              ? entityError.message
              : 'An unexpected error occurred while loading the entity schema.'}
          </p>
          <Link
            to="/admin/entities"
            className="mt-4 inline-flex items-center gap-1 rounded text-sm font-medium text-red-700 underline hover:text-red-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500"
          >
            ← Back to entities
          </Link>
        </div>
      </div>
    );
  }

  // Guard: entity must be loaded
  if (!entity) {
    return null;
  }

  // ── Main Render ─────────────────────────────────────────────
  return (
    <div className="mx-auto max-w-4xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ── Breadcrumb Navigation ─────────────────────────── */}
      <nav aria-label="Breadcrumb" className="mb-4">
        <ol className="flex items-center gap-2 text-sm text-gray-500">
          <li>
            <Link
              to="/admin/entities"
              className="rounded hover:text-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            >
              Entities
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li>
            <Link
              to={`/admin/entities/${entityId}/data`}
              className="rounded hover:text-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            >
              {entity.label}
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li aria-current="page" className="font-medium text-gray-900">
            Create Record
          </li>
        </ol>
      </nav>

      {/* ── Page Header ───────────────────────────────────── */}
      <div className="mb-6 flex items-center gap-3">
        {entity.iconName && (
          <span
            className="flex h-10 w-10 items-center justify-center rounded-lg text-white"
            style={{
              backgroundColor: entity.color || '#6366f1',
            }}
            aria-hidden="true"
          >
            <i className={entity.iconName} />
          </span>
        )}
        <div>
          <h1 className="text-2xl font-bold text-gray-900">
            Create {entity.label} Record
          </h1>
          <p className="text-sm text-gray-500">
            Fill in the fields below to create a new{' '}
            {entity.label.toLowerCase()} record.
          </p>
        </div>
      </div>

      {/* ── Mutation-Level Error Banner ───────────────────── */}
      {mutationIsError && !validation && (
        <div
          role="alert"
          className="mb-4 rounded-lg border border-red-200 bg-red-50 p-4"
        >
          <p className="text-sm font-medium text-red-800">
            {mutationError instanceof Error
              ? mutationError.message
              : 'An error occurred while creating the record.'}
          </p>
        </div>
      )}

      {/* ── Success Feedback (brief, before navigation) ──── */}
      {isSuccess && createdRecord && (
        <div
          role="status"
          className="mb-4 rounded-lg border border-green-200 bg-green-50 p-4"
        >
          <p className="text-sm font-medium text-green-800">
            Record created successfully. Redirecting…
          </p>
        </div>
      )}

      {/* ── Dynamic Record Creation Form ──────────────────── */}
      <DynamicForm
        labelMode="stacked"
        fieldMode="form"
        name="record-create-form"
        validation={validation}
        showValidation
        onSubmit={() => {
          void handleSubmit();
        }}
      >
        <div className="space-y-6">
          {visibleFields.map((field) => {
            const fieldErrors =
              validation?.errors.filter(
                (e) => e.propertyName === field.name,
              ) ?? [];
            const isCheckbox =
              field.fieldType === FieldType.CheckboxField;

            return (
              <div key={field.id} className="space-y-1.5">
                {/* ── Label (checkbox uses inline layout) ── */}
                {isCheckbox ? (
                  <div className="flex items-center gap-2">
                    {renderFieldInput(field)}
                    <label
                      htmlFor={`field-${field.name}`}
                      className="text-sm font-medium text-gray-700"
                    >
                      {field.label}
                      {field.required && (
                        <span
                          className="ms-0.5 text-red-500"
                          aria-hidden="true"
                        >
                          *
                        </span>
                      )}
                    </label>
                  </div>
                ) : (
                  <>
                    <label
                      htmlFor={`field-${field.name}`}
                      className="block text-sm font-medium text-gray-700"
                    >
                      {field.label}
                      {field.required && (
                        <span
                          className="ms-0.5 text-red-500"
                          aria-hidden="true"
                        >
                          *
                        </span>
                      )}
                    </label>
                    {renderFieldInput(field)}
                  </>
                )}

                {/* ── Help Text ───────────────────────────── */}
                {field.helpText && (
                  <p
                    id={`help-${field.name}`}
                    className="text-xs text-gray-500"
                  >
                    {field.helpText}
                  </p>
                )}

                {/* ── Description ─────────────────────────── */}
                {field.description && (
                  <p className="text-xs text-gray-400">
                    {field.description}
                  </p>
                )}

                {/* ── Field-Level Validation Errors ───────── */}
                {fieldErrors.length > 0 && (
                  <div role="alert" className="text-xs text-red-600">
                    {fieldErrors.map((err, idx) => (
                      <p key={`${err.propertyName}-${idx}`}>
                        {err.message}
                      </p>
                    ))}
                  </div>
                )}
              </div>
            );
          })}

          {/* ── Empty Fields Notice ───────────────────────── */}
          {visibleFields.length === 0 && (
            <p className="py-8 text-center text-sm text-gray-500">
              This entity has no editable fields.
            </p>
          )}
        </div>

        {/* ── Action Buttons ──────────────────────────────── */}
        <div className="mt-8 flex items-center gap-3 border-t border-gray-200 pt-6">
          <button
            type="submit"
            disabled={isPending}
            className={
              'inline-flex items-center gap-2 rounded-md px-4 py-2 text-sm font-semibold text-white shadow-sm transition-colors ' +
              'bg-blue-600 hover:bg-blue-700 ' +
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 ' +
              'disabled:cursor-not-allowed disabled:opacity-60'
            }
          >
            {isPending ? (
              <>
                <span
                  className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                  aria-hidden="true"
                />
                Creating…
              </>
            ) : (
              'Save Record'
            )}
          </button>
          <Link
            to={`/admin/entities/${entityId}/data`}
            className={
              'inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors ' +
              'hover:bg-gray-50 ' +
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2'
            }
          >
            Cancel
          </Link>
        </div>
      </DynamicForm>
    </div>
  );
}
