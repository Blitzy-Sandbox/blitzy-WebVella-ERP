/**
 * Input Validation Functions — WebVella ERP Frontend
 *
 * Pure, browser-side validation utilities that replicate the server-side
 * validation logic previously found in:
 *   - WebVella.Erp.Web/Utils/DataUtils.cs   (ValidateValueToFieldType, GetFilterTypesForFieldType)
 *   - WebVella.Erp/Api/EntityManager.cs      (entity/field name constraints)
 *   - WebVella.Erp/Api/Models/ValidationUtility.cs (NAME_VALIDATION_PATTERN)
 *   - WebVella.Erp/Utilities/TextExtensions.cs     (IsEmail)
 *   - WebVella.Erp/Api/Models/FieldTypes/FieldType.cs (21 field types)
 *
 * Rules:
 *   - All functions are pure — zero side-effects, zero async calls
 *   - TypeScript strict mode compliant — every parameter and return fully typed
 *   - Named exports only — no default export
 *   - Email regex preserved exactly from DataUtils.cs line 114
 *   - Entity/field name regex from ValidationUtility.cs NAME_VALIDATION_PATTERN
 */

import { FieldType, APP_DEFAULTS } from './constants';

// =============================================================================
// Interfaces
// =============================================================================

/**
 * Result of a single field value validation.
 * Mirrors the out-parameters of DataUtils.ValidateValueToFieldType:
 *   OutValue  → coercedValue
 *   errorList → errors
 */
export interface ValidationResult {
  /** Whether the validation passed without errors */
  isValid: boolean;
  /** List of human-readable error messages (empty when valid) */
  errors: string[];
  /** The value after type coercion (null when invalid or empty) */
  coercedValue: unknown;
}

/**
 * Options that control constraint-level validation for a single field.
 * Extracted from EntityManager.cs field property validation patterns.
 */
export interface FieldValidationOptions {
  /** Field is required — null/empty values produce an error */
  required?: boolean;
  /** Value must be unique (client hint only; actual enforcement is server-side) */
  unique?: boolean;
  /** Field is indexed for search */
  searchable?: boolean;
  /** Maximum string length (TextField default 200 in monolith) */
  maxLength?: number;
  /** Minimum numeric value for number-like fields */
  minValue?: number;
  /** Maximum numeric value for number-like fields */
  maxValue?: number;
  /** Custom regex pattern the value must match */
  pattern?: string;
  /** Valid option values for SelectField / MultiSelectField */
  options?: string[];
  /** Decimal precision for CurrencyField / NumberField / PercentField */
  decimalPlaces?: number;
}

// =============================================================================
// Internal helpers
// =============================================================================

/**
 * Returns `true` when a value is considered "empty" in the monolith sense:
 * null, undefined, or the empty string.
 */
function isEmpty(value: unknown): boolean {
  return value === null || value === undefined || (typeof value === 'string' && value === '');
}

/**
 * Safely converts an unknown value to its string representation.
 */
function toStr(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }
  return String(value);
}

/**
 * Creates a successful ValidationResult with a coerced value.
 */
function ok(coercedValue: unknown): ValidationResult {
  return { isValid: true, errors: [], coercedValue };
}

/**
 * Creates a failed ValidationResult with one or more error messages.
 */
function fail(errors: string[], coercedValue: unknown = null): ValidationResult {
  return { isValid: false, errors, coercedValue };
}

// =============================================================================
// Individual type validators (Phase 3 from AAP)
// =============================================================================

/**
 * Exact email regex from DataUtils.cs line 114.
 * Preserved character-for-character from the C# pattern:
 *   `[a-z0-9!#$%&'*+/=?^_\`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_\`{|}~-]+)*
 *    @(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?`
 */
const EMAIL_REGEX =
  /[a-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?/;

/**
 * UUID format regex matching Guid.TryParse behaviour from DataUtils.cs line 133.
 * Accepts all RFC-4122 variants (not restricted to v4).
 */
const GUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Entity / field name pattern from ValidationUtility.cs NAME_VALIDATION_PATTERN.
 * Must start with a lowercase letter, contain only lowercase alphanumeric and
 * underscores, must not contain consecutive underscores, and must end with a
 * lowercase letter or digit.
 */
const NAME_VALIDATION_PATTERN = /^[a-z](?!.*__)[a-z0-9_]*[a-z0-9]$/;

/**
 * Phone number pattern: allows digits, spaces, dashes, plus, parentheses,
 * and dots. At least one digit required.
 */
const PHONE_REGEX = /^[+]?[\d\s\-().]+$/;

/**
 * Validates an email address using the exact regex from DataUtils.cs line 114.
 * Also references TextExtensions.cs IsEmail() which uses MailAddress validation.
 *
 * @param value - The string to test
 * @returns `true` when the value matches the email regex
 */
export function isValidEmail(value: string): boolean {
  if (typeof value !== 'string' || value.trim().length === 0) {
    return false;
  }
  return EMAIL_REGEX.test(value.toLowerCase());
}

/**
 * Validates a URL by attempting to construct a `URL` object.
 * Matches UrlField validation in EntityManager — any parseable URL is valid.
 *
 * @param value - The string to test
 * @returns `true` when the value is a parseable URL with http(s) or ftp scheme
 */
export function isValidUrl(value: string): boolean {
  if (typeof value !== 'string' || value.trim().length === 0) {
    return false;
  }
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:' || url.protocol === 'ftp:';
  } catch {
    return false;
  }
}

/**
 * Validates a phone number with basic pattern matching.
 * Accepts digits, spaces, dashes, plus sign, parentheses and dots.
 *
 * @param value - The string to test
 * @returns `true` when the value matches the phone pattern
 */
export function isValidPhone(value: string): boolean {
  if (typeof value !== 'string' || value.trim().length === 0) {
    return false;
  }
  return PHONE_REGEX.test(value.trim());
}

/**
 * Validates a GUID/UUID string matching Guid.TryParse from DataUtils.cs line 133.
 * Accepts standard 8-4-4-4-12 hexadecimal format, case-insensitive.
 *
 * @param value - The string to test
 * @returns `true` when the value matches UUID format
 */
export function isValidGuid(value: string): boolean {
  if (typeof value !== 'string' || value.trim().length === 0) {
    return false;
  }
  return GUID_REGEX.test(value.trim());
}

/**
 * Checks whether a value can be interpreted as a finite number.
 * Mirrors Decimal.TryParse behaviour from DataUtils.cs.
 *
 * @param value - The value to test (string, number, or other)
 * @returns `true` when the value is a finite number or parseable as one
 */
export function isValidNumber(value: unknown): boolean {
  if (value === null || value === undefined || (typeof value === 'string' && value.trim() === '')) {
    return false;
  }
  const parsed = Number(value);
  return !Number.isNaN(parsed) && Number.isFinite(parsed);
}

/**
 * Checks whether a value can be interpreted as a valid Date.
 * Mirrors DateTime.TryParse from DataUtils.cs.
 *
 * @param value - The value to test
 * @returns `true` when the value represents a valid date
 */
export function isValidDate(value: unknown): boolean {
  if (value === null || value === undefined || (typeof value === 'string' && value.trim() === '')) {
    return false;
  }
  if (value instanceof Date) {
    return !Number.isNaN(value.getTime());
  }
  const parsed = new Date(String(value));
  return !Number.isNaN(parsed.getTime());
}

/**
 * Checks whether a value can be interpreted as a boolean.
 * Mirrors Boolean.TryParse from DataUtils.cs — accepts actual booleans
 * and the strings "true" / "false" (case-insensitive).
 *
 * @param value - The value to test
 * @returns `true` when the value is a boolean or parseable as one
 */
export function isValidBoolean(value: unknown): boolean {
  if (typeof value === 'boolean') {
    return true;
  }
  if (typeof value === 'string') {
    const lower = value.toLowerCase().trim();
    return lower === 'true' || lower === 'false';
  }
  return false;
}

// =============================================================================
// Constraint helpers (exported utilities)
// =============================================================================

/**
 * Checks whether a value satisfies a "required" constraint.
 * A value is considered present when it is not null, not undefined,
 * and — if a string — not empty / whitespace-only.
 *
 * @param value - The value to check
 * @returns `true` when the value is present
 */
export function isRequired(value: unknown): boolean {
  if (value === null || value === undefined) {
    return false;
  }
  if (typeof value === 'string') {
    return value.trim().length > 0;
  }
  if (Array.isArray(value)) {
    return value.length > 0;
  }
  return true;
}

/**
 * Checks whether a numeric value falls within an inclusive range.
 *
 * @param value  - The numeric value
 * @param min    - Lower bound (inclusive). Omit to skip lower check.
 * @param max    - Upper bound (inclusive). Omit to skip upper check.
 * @returns `true` when the value is within bounds
 */
export function isInRange(value: number, min?: number, max?: number): boolean {
  if (!Number.isFinite(value)) {
    return false;
  }
  if (min !== undefined && min !== null && value < min) {
    return false;
  }
  if (max !== undefined && max !== null && value > max) {
    return false;
  }
  return true;
}

/**
 * Checks whether a string's length does not exceed `maxLength`.
 *
 * @param value     - The string to measure
 * @param maxLength - Maximum allowed length
 * @returns `true` when the string length is at most `maxLength`
 */
export function isWithinLength(value: string, maxLength: number): boolean {
  if (typeof value !== 'string') {
    return false;
  }
  return value.length <= maxLength;
}

/**
 * Validates a password for strength.
 * Requires at least 8 characters, one uppercase letter, one lowercase letter,
 * one digit, and one special character.
 *
 * @param value - The password string
 * @returns `true` when the password meets strength criteria
 */
export function isStrongPassword(value: string): boolean {
  if (typeof value !== 'string' || value.length < 8) {
    return false;
  }
  const hasUpper = /[A-Z]/.test(value);
  const hasLower = /[a-z]/.test(value);
  const hasDigit = /\d/.test(value);
  const hasSpecial = /[^A-Za-z0-9]/.test(value);
  return hasUpper && hasLower && hasDigit && hasSpecial;
}

// =============================================================================
// Field value validation (Phase 1 — from DataUtils.cs lines 14-214)
// =============================================================================

/**
 * Validates and coerces a value based on its field type, replicating
 * DataUtils.ValidateValueToFieldType from the monolith.
 *
 * @param fieldType - One of the FieldType constant string keys
 * @param value     - The raw input value
 * @param options   - Optional constraint options
 * @returns A ValidationResult with coerced value and any errors
 */
export function validateFieldValue(
  fieldType: string,
  value: unknown,
  options?: FieldValidationOptions,
): ValidationResult {
  switch (fieldType) {
    // -----------------------------------------------------------------
    // AutoNumberField (DataUtils.cs lines 23-42)
    // Accept null/empty → null; must parse as number (Decimal in C#)
    // -----------------------------------------------------------------
    case FieldType.AutoNumberField: {
      if (isEmpty(value)) {
        return ok(null);
      }
      if (typeof value === 'number' && Number.isFinite(value)) {
        return ok(value);
      }
      const parsed = Number(value);
      if (!Number.isNaN(parsed) && Number.isFinite(parsed)) {
        return ok(parsed);
      }
      return fail(['Value should be a decimal']);
    }

    // -----------------------------------------------------------------
    // CheckboxField (DataUtils.cs lines 43-62)
    // Accept null/empty → null; must parse as boolean
    // -----------------------------------------------------------------
    case FieldType.CheckboxField: {
      if (isEmpty(value)) {
        return ok(null);
      }
      if (typeof value === 'boolean') {
        return ok(value);
      }
      const str = toStr(value).toLowerCase().trim();
      if (str === 'true') {
        return ok(true);
      }
      if (str === 'false') {
        return ok(false);
      }
      return fail(['Value should be a boolean']);
    }

    // -----------------------------------------------------------------
    // CurrencyField / NumberField / PercentField (DataUtils.cs lines 63-84)
    // Accept null/empty → null; must parse as number
    // -----------------------------------------------------------------
    case FieldType.CurrencyField:
    case FieldType.NumberField:
    case FieldType.PercentField: {
      if (isEmpty(value)) {
        return ok(null);
      }
      if (typeof value === 'number' && Number.isFinite(value)) {
        const coerced = applyDecimalPlaces(value, options?.decimalPlaces);
        return ok(coerced);
      }
      const parsed = Number(value);
      if (!Number.isNaN(parsed) && Number.isFinite(parsed)) {
        const coerced = applyDecimalPlaces(parsed, options?.decimalPlaces);
        return ok(coerced);
      }
      return fail(['Value should be a decimal']);
    }

    // -----------------------------------------------------------------
    // DateField / DateTimeField (DataUtils.cs lines 85-104)
    // Accept null/empty → null; must parse as Date
    // -----------------------------------------------------------------
    case FieldType.DateField:
    case FieldType.DateTimeField: {
      if (isEmpty(value)) {
        return ok(null);
      }
      if (value instanceof Date && !Number.isNaN(value.getTime())) {
        return ok(value);
      }
      const date = new Date(String(value));
      if (!Number.isNaN(date.getTime())) {
        return ok(date);
      }
      return fail(['Value should be a DateTime']);
    }

    // -----------------------------------------------------------------
    // EmailField (DataUtils.cs lines 106-121)
    // Accept null/empty → empty string; validate with exact regex
    // -----------------------------------------------------------------
    case FieldType.EmailField: {
      if (isEmpty(value)) {
        return ok('');
      }
      const str = toStr(value);
      if (str.trim().length > 0 && !EMAIL_REGEX.test(str.toLowerCase())) {
        return fail(['Value is not a valid email!'], str);
      }
      return ok(str);
    }

    // -----------------------------------------------------------------
    // GuidField (DataUtils.cs lines 123-141)
    // Accept null/empty → null; must match UUID format
    // -----------------------------------------------------------------
    case FieldType.GuidField: {
      if (isEmpty(value)) {
        return ok(null);
      }
      const str = toStr(value).trim();
      if (GUID_REGEX.test(str)) {
        return ok(str.toLowerCase());
      }
      return fail(['Value should be a Guid']);
    }

    // -----------------------------------------------------------------
    // HtmlField (DataUtils.cs lines 143-170)
    // Accept null/empty → empty string; validate HTML structure
    // -----------------------------------------------------------------
    case FieldType.HtmlField: {
      if (isEmpty(value)) {
        return ok('');
      }
      const str = toStr(value);
      /* In the browser, use DOMParser for basic HTML validation. */
      if (typeof DOMParser !== 'undefined') {
        try {
          const parser = new DOMParser();
          const doc = parser.parseFromString(str, 'text/html');
          const parseError = doc.querySelector('parsererror');
          if (parseError) {
            return fail(['Invalid HTML content'], str);
          }
        } catch {
          return fail(['Invalid HTML content'], str);
        }
      }
      return ok(str);
    }

    // -----------------------------------------------------------------
    // MultiSelectField (DataUtils.cs lines 171-187)
    // Accept null/empty → empty array; string → wrap in array
    // -----------------------------------------------------------------
    case FieldType.MultiSelectField: {
      if (isEmpty(value)) {
        return ok([]);
      }
      if (Array.isArray(value)) {
        return ok(value.map(String));
      }
      return ok([toStr(value)]);
    }

    // -----------------------------------------------------------------
    // String-like fields (DataUtils.cs lines 189-207)
    // FileField, ImageField, MultiLineTextField, PasswordField,
    // PhoneField, SelectField, TextField, UrlField
    // Accept null/empty → empty string; coerce to string
    // -----------------------------------------------------------------
    case FieldType.FileField:
    case FieldType.ImageField:
    case FieldType.MultiLineTextField:
    case FieldType.PasswordField:
    case FieldType.PhoneField:
    case FieldType.SelectField:
    case FieldType.TextField:
    case FieldType.UrlField: {
      if (isEmpty(value)) {
        return ok('');
      }
      return ok(toStr(value));
    }

    // -----------------------------------------------------------------
    // RelationField — Validate as GUID
    // -----------------------------------------------------------------
    case FieldType.RelationField: {
      if (isEmpty(value)) {
        return ok(null);
      }
      const str = toStr(value).trim();
      if (GUID_REGEX.test(str)) {
        return ok(str.toLowerCase());
      }
      return fail(['Value should be a Guid']);
    }

    // -----------------------------------------------------------------
    // GeographyField — Validate GeoJSON format
    // -----------------------------------------------------------------
    case FieldType.GeographyField: {
      if (isEmpty(value)) {
        return ok(null);
      }
      return validateGeoJson(value);
    }

    // -----------------------------------------------------------------
    // Default fallback (DataUtils.cs lines 208-212)
    // -----------------------------------------------------------------
    default:
      return ok(value);
  }
}

// =============================================================================
// GeoJSON helper
// =============================================================================

/**
 * Validates that a value is a valid GeoJSON geometry object.
 * Accepted types: Point, MultiPoint, LineString, MultiLineString,
 * Polygon, MultiPolygon, GeometryCollection, Feature, FeatureCollection.
 */
function validateGeoJson(value: unknown): ValidationResult {
  const VALID_GEO_TYPES: ReadonlySet<string> = new Set([
    'Point',
    'MultiPoint',
    'LineString',
    'MultiLineString',
    'Polygon',
    'MultiPolygon',
    'GeometryCollection',
    'Feature',
    'FeatureCollection',
  ]);

  let obj: Record<string, unknown>;
  if (typeof value === 'string') {
    try {
      obj = JSON.parse(value) as Record<string, unknown>;
    } catch {
      return fail(['Value is not valid GeoJSON']);
    }
  } else if (typeof value === 'object' && value !== null) {
    obj = value as Record<string, unknown>;
  } else {
    return fail(['Value is not valid GeoJSON']);
  }

  if (!obj.type || typeof obj.type !== 'string' || !VALID_GEO_TYPES.has(obj.type)) {
    return fail(['Value is not valid GeoJSON']);
  }

  /* Point / LineString / Polygon etc. require "coordinates" */
  const needsCoordinates = obj.type !== 'Feature' && obj.type !== 'FeatureCollection' && obj.type !== 'GeometryCollection';
  if (needsCoordinates && !Array.isArray(obj.coordinates)) {
    return fail(['GeoJSON geometry must contain a coordinates array']);
  }

  return ok(obj);
}

/**
 * Rounds a numeric value to the specified number of decimal places.
 * Returns the value unchanged when `decimalPlaces` is not provided.
 */
function applyDecimalPlaces(value: number, decimalPlaces?: number): number {
  if (decimalPlaces === undefined || decimalPlaces === null || decimalPlaces < 0) {
    return value;
  }
  const factor = Math.pow(10, decimalPlaces);
  return Math.round(value * factor) / factor;
}

// =============================================================================
// Filter type mapping (Phase 2 — from DataUtils.cs lines 216-276)
// =============================================================================

/** Numeric filter operators */
const NUMERIC_FILTERS = ['EQ', 'NOT', 'LT', 'LTE', 'GT', 'GTE', 'BETWEEN', 'NOTBETWEEN'] as const;

/** Date filter operators (identical to numeric) */
const DATE_FILTERS = ['EQ', 'NOT', 'LT', 'LTE', 'GT', 'GTE', 'BETWEEN', 'NOTBETWEEN'] as const;

/** Default text-like filter operators */
const TEXT_FILTERS = ['STARTSWITH', 'CONTAINS', 'EQ', 'NOT', 'REGEX', 'FTS'] as const;

/**
 * Returns the set of filter operator names applicable to a given field type.
 * Mirrors DataUtils.GetFilterTypesForFieldType (lines 216-276).
 *
 * @param fieldType - One of the FieldType constant string keys
 * @returns An array of filter type name strings
 */
export function getFilterTypesForFieldType(fieldType: string): string[] {
  switch (fieldType) {
    case FieldType.CheckboxField:
      return ['EQ'];

    case FieldType.AutoNumberField:
    case FieldType.CurrencyField:
    case FieldType.NumberField:
    case FieldType.PercentField:
      return [...NUMERIC_FILTERS];

    case FieldType.DateField:
    case FieldType.DateTimeField:
      return [...DATE_FILTERS];

    case FieldType.GuidField:
      return ['EQ'];

    case FieldType.MultiSelectField:
      return ['CONTAINS'];

    default:
      return [...TEXT_FILTERS];
  }
}

// =============================================================================
// Field constraint validation (Phase 4 — from EntityManager.cs)
// =============================================================================

/**
 * Validates a field value against the constraint rules defined in
 * EntityManager.cs (required, maxLength, minValue, maxValue, pattern, options,
 * decimalPlaces). This is complementary to `validateFieldValue` which handles
 * type coercion.
 *
 * @param value     - The raw field value
 * @param fieldType - One of the FieldType constant string keys
 * @param options   - Constraint options from the field definition
 * @returns A ValidationResult with any constraint violation errors
 */
export function validateFieldConstraints(
  value: unknown,
  fieldType: string,
  options: FieldValidationOptions,
): ValidationResult {
  const errors: string[] = [];

  /* ---- Required check ---- */
  if (options.required) {
    if (!isRequired(value)) {
      errors.push('This field is required');
    }
  }

  /* Early return when the value is empty and not required */
  if (isEmpty(value) && !options.required) {
    return errors.length > 0 ? fail(errors, value) : ok(value);
  }

  /* ---- MaxLength for string-like fields ---- */
  if (options.maxLength !== undefined && options.maxLength !== null) {
    const isStringType =
      fieldType === FieldType.TextField ||
      fieldType === FieldType.MultiLineTextField ||
      fieldType === FieldType.EmailField ||
      fieldType === FieldType.UrlField ||
      fieldType === FieldType.PhoneField ||
      fieldType === FieldType.HtmlField ||
      fieldType === FieldType.PasswordField;

    if (isStringType && typeof value === 'string' && value.length > options.maxLength) {
      errors.push(`Value must not exceed ${options.maxLength} characters`);
    }
  }

  /* ---- MinValue / MaxValue for numeric fields ---- */
  if (options.minValue !== undefined || options.maxValue !== undefined) {
    const isNumericType =
      fieldType === FieldType.AutoNumberField ||
      fieldType === FieldType.CurrencyField ||
      fieldType === FieldType.NumberField ||
      fieldType === FieldType.PercentField;

    if (isNumericType) {
      const num = typeof value === 'number' ? value : Number(value);
      if (Number.isFinite(num)) {
        if (options.minValue !== undefined && options.minValue !== null && num < options.minValue) {
          errors.push(`Value must be at least ${options.minValue}`);
        }
        if (options.maxValue !== undefined && options.maxValue !== null && num > options.maxValue) {
          errors.push(`Value must be at most ${options.maxValue}`);
        }
      }
    }
  }

  /* ---- Custom regex pattern ---- */
  if (options.pattern) {
    const str = toStr(value);
    if (str.length > 0) {
      try {
        const regex = new RegExp(options.pattern);
        if (!regex.test(str)) {
          errors.push('Value does not match the required pattern');
        }
      } catch {
        errors.push('Invalid validation pattern configured');
      }
    }
  }

  /* ---- Options membership for select / multiselect ---- */
  if (options.options && options.options.length > 0) {
    const allowedSet = new Set(options.options);

    if (fieldType === FieldType.SelectField) {
      const str = toStr(value);
      if (str.length > 0 && !allowedSet.has(str)) {
        errors.push('Value is not a valid option');
      }
    }

    if (fieldType === FieldType.MultiSelectField) {
      const arr = Array.isArray(value) ? value : [value];
      for (const item of arr) {
        const str = toStr(item);
        if (str.length > 0 && !allowedSet.has(str)) {
          errors.push(`"${str}" is not a valid option`);
        }
      }
    }
  }

  /* ---- Decimal places validation ---- */
  if (options.decimalPlaces !== undefined && options.decimalPlaces !== null) {
    const isDecimalType =
      fieldType === FieldType.CurrencyField ||
      fieldType === FieldType.NumberField ||
      fieldType === FieldType.PercentField;

    if (isDecimalType && value !== null && value !== undefined) {
      const num = typeof value === 'number' ? value : Number(value);
      if (Number.isFinite(num)) {
        const parts = String(num).split('.');
        if (parts.length === 2 && parts[1].length > options.decimalPlaces) {
          errors.push(`Value must have at most ${options.decimalPlaces} decimal places`);
        }
      }
    }
  }

  return errors.length > 0 ? fail(errors, value) : ok(value);
}

// =============================================================================
// Form-level validation (Phase 5)
// =============================================================================

/**
 * Validates every field in a form definition, returning a map of
 * field name → error messages. An empty map indicates full validity.
 *
 * @param fields - Array of field definitions with name, type, value, and options
 * @returns Record mapping field names to their error arrays
 */
export function validateForm(
  fields: Array<{
    name: string;
    fieldType: string;
    value: unknown;
    options?: FieldValidationOptions;
  }>,
): Record<string, string[]> {
  const result: Record<string, string[]> = {};

  for (const field of fields) {
    const typeResult = validateFieldValue(field.fieldType, field.value, field.options);
    const constraintResult = field.options
      ? validateFieldConstraints(field.value, field.fieldType, field.options)
      : ok(field.value);

    const combined = [...typeResult.errors, ...constraintResult.errors];
    if (combined.length > 0) {
      result[field.name] = combined;
    }
  }

  return result;
}

/**
 * Returns `true` when the errors map contains at least one field with errors.
 *
 * @param errors - The validation errors map returned by `validateForm`
 * @returns `true` when any field has errors
 */
export function hasValidationErrors(errors: Record<string, string[]>): boolean {
  return Object.keys(errors).some((key) => errors[key].length > 0);
}

// =============================================================================
// Entity / Field name validation (Phase 6 — from EntityManager.cs + ValidationUtility)
// =============================================================================

/**
 * Validates an entity name against the constraints from EntityManager.cs
 * and ValidationUtility.ValidateName:
 *   - Must start with a lowercase letter
 *   - Only lowercase alphanumeric and underscores allowed
 *   - Must not contain consecutive underscores
 *   - Must not end with an underscore
 *   - Minimum 2 characters
 *   - Maximum 63 characters (PostgreSQL identifier limit — APP_DEFAULTS.ENTITY_NAME_MAX_LENGTH)
 *   - Cannot start with 'wv_' prefix (reserved for system entities)
 *
 * @param name - The entity name to validate
 * @returns `true` when the name satisfies all constraints
 */
export function isValidEntityName(name: string): boolean {
  if (typeof name !== 'string') {
    return false;
  }

  const trimmed = name.trim();

  /* Length constraints: min 2, max ENTITY_NAME_MAX_LENGTH (63) */
  if (trimmed.length < 2 || trimmed.length > APP_DEFAULTS.ENTITY_NAME_MAX_LENGTH) {
    return false;
  }

  /* Reserved prefix check — 'wv_' is reserved for system entities */
  if (trimmed.startsWith('wv_')) {
    return false;
  }

  /* Pattern: starts with lowercase letter, only lowercase alnum + underscore,
     no consecutive underscores, ends with letter or digit */
  return NAME_VALIDATION_PATTERN.test(trimmed);
}

/**
 * Validates a field name using the same rules as entity names.
 * From EntityManager.cs — field names follow the identical PostgreSQL identifier
 * constraints enforced by ValidationUtility.ValidateName.
 *
 * @param name - The field name to validate
 * @returns `true` when the name satisfies all constraints
 */
export function isValidFieldName(name: string): boolean {
  return isValidEntityName(name);
}
