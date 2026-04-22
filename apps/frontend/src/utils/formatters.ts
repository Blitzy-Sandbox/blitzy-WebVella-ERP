/**
 * Formatting Utilities — WebVella ERP Frontend
 *
 * Production-ready date, number, currency, and template formatting functions
 * replacing the monolith's server-side formatting patterns:
 *   - DateTimeExtensions.cs    (ConvertToAppDate, ConvertAppDateToUtc, ClearKind)
 *   - RenderService.cs         (GetPathTypeIcon, RenderHtmlWithTemplate, GetSnippetFromHtml)
 *   - Helpers.cs               (Currency catalog reference)
 *   - Definitions.cs           (CurrencyType class, CurrencySymbolPlacement enum)
 *
 * Rules:
 *   - No server-side dependencies — all functions work in the browser
 *   - Pure functions — no side effects, no global state mutations
 *   - TypeScript strict mode — all parameters and return types fully typed
 *   - Intl API — uses the browser Intl API for locale-aware formatting
 *   - No jQuery — zero DOM manipulation libraries
 *   - Named exports only — no default export
 *   - Constants imported from ./constants for default locale/timezone/currency
 */

import {
  APP_DEFAULTS,
  COMMON_CURRENCIES,
  CurrencySymbolPlacement,
} from './constants';
import type { CurrencyType } from './constants';
import { getNestedProperty } from './helpers';

// =============================================================================
// Internal Helpers
// =============================================================================

/**
 * Safely parses a date value into a Date object.
 * Returns `null` if the input is null, undefined, or an invalid date string.
 */
function parseDate(date: Date | string | null | undefined): Date | null {
  if (date == null) {
    return null;
  }
  if (date instanceof Date) {
    return isNaN(date.getTime()) ? null : date;
  }
  const parsed = new Date(date);
  return isNaN(parsed.getTime()) ? null : parsed;
}

/**
 * Extracts a numeric date-part value from Intl.DateTimeFormat.formatToParts().
 *
 * @param parts - The array of formatted parts.
 * @param type  - The part type to extract (e.g. 'year', 'month').
 * @returns The numeric value of the part, or 0 if not found.
 */
function getDatePart(parts: Intl.DateTimeFormatPart[], type: string): number {
  const part = parts.find((p) => p.type === type);
  return part ? parseInt(part.value, 10) : 0;
}

// =============================================================================
// Phase 1: Date/Time Formatting (from DateTimeExtensions.cs)
// =============================================================================

/**
 * Formats a date value into a locale-aware date string.
 *
 * Replaces the monolith's server-side date rendering with browser-native
 * `Intl.DateTimeFormat` for correct localisation and timezone handling.
 *
 * @param date     - Date value to format (Date object, ISO string, or null).
 * @param format   - Output format: 'short' (MM/DD/YYYY), 'long' (Month DD, YYYY),
 *                   'iso' (ISO 8601 date), 'relative' (time ago). Defaults to 'short'.
 * @param timezone - IANA timezone identifier. Defaults to APP_DEFAULTS.TIMEZONE.
 * @returns The formatted date string, or empty string if date is null/invalid.
 */
export function formatDate(
  date: Date | string | null,
  format?: string,
  timezone?: string
): string {
  const d = parseDate(date);
  if (d === null) {
    return '';
  }

  const tz = timezone ?? APP_DEFAULTS.TIMEZONE;
  const locale = APP_DEFAULTS.LOCALE;

  switch (format) {
    case 'long':
      return new Intl.DateTimeFormat(locale, {
        timeZone: tz,
        year: 'numeric',
        month: 'long',
        day: 'numeric',
      }).format(d);

    case 'iso':
      return d.toISOString().split('T')[0];

    case 'relative':
      return formatRelativeTime(d);

    case 'short':
    default:
      return new Intl.DateTimeFormat(locale, {
        timeZone: tz,
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
      }).format(d);
  }
}

/**
 * Formats a date value into a locale-aware date-and-time string.
 *
 * Same as `formatDate` but includes the time component. Uses
 * `Intl.DateTimeFormat` with both dateStyle and timeStyle equivalents.
 *
 * @param date     - Date value to format.
 * @param format   - Output format: 'short', 'long', 'iso', 'relative'. Defaults to 'short'.
 * @param timezone - IANA timezone identifier. Defaults to APP_DEFAULTS.TIMEZONE.
 * @returns The formatted date-time string, or empty string if date is null/invalid.
 */
export function formatDateTime(
  date: Date | string | null,
  format?: string,
  timezone?: string
): string {
  const d = parseDate(date);
  if (d === null) {
    return '';
  }

  const tz = timezone ?? APP_DEFAULTS.TIMEZONE;
  const locale = APP_DEFAULTS.LOCALE;

  switch (format) {
    case 'long':
      return new Intl.DateTimeFormat(locale, {
        timeZone: tz,
        year: 'numeric',
        month: 'long',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
      }).format(d);

    case 'iso':
      return d.toISOString();

    case 'relative':
      return formatRelativeTime(d);

    case 'short':
    default:
      return new Intl.DateTimeFormat(locale, {
        timeZone: tz,
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
      }).format(d);
  }
}

/**
 * Formats a date value into a time-only string (no date component).
 *
 * @param date     - Date value to format.
 * @param format   - Output format: 'short' (HH:MM), 'long' (HH:MM:SS),
 *                   'iso' (HH:MM:SS.sss). Defaults to 'short'.
 * @param timezone - IANA timezone identifier. Defaults to APP_DEFAULTS.TIMEZONE.
 * @returns The formatted time string, or empty string if date is null/invalid.
 */
export function formatTime(
  date: Date | string | null,
  format?: string,
  timezone?: string
): string {
  const d = parseDate(date);
  if (d === null) {
    return '';
  }

  const tz = timezone ?? APP_DEFAULTS.TIMEZONE;
  const locale = APP_DEFAULTS.LOCALE;

  switch (format) {
    case 'long':
      return new Intl.DateTimeFormat(locale, {
        timeZone: tz,
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
      }).format(d);

    case 'iso': {
      const isoStr = d.toISOString();
      return isoStr.split('T')[1].replace('Z', '');
    }

    case 'short':
    default:
      return new Intl.DateTimeFormat(locale, {
        timeZone: tz,
        hour: '2-digit',
        minute: '2-digit',
      }).format(d);
  }
}

/**
 * Converts a UTC Date to the application's configured timezone.
 *
 * Replaces `DateTimeExtensions.ConvertToAppDate` (lines 21-37).
 * The monolith used `TimeZoneInfo.ConvertTimeBySystemTimeZoneId` with Windows
 * timezone IDs; this implementation uses IANA timezone strings via `Intl`.
 *
 * The returned Date's local-time getters (getFullYear, getMonth, getDate,
 * getHours, getMinutes, getSeconds) reflect the wall-clock time in the
 * target timezone.
 *
 * @param date     - The UTC Date to convert.
 * @param timezone - IANA timezone identifier. Defaults to APP_DEFAULTS.TIMEZONE.
 * @returns A new Date with components adjusted to the target timezone.
 */
export function convertToAppDate(date: Date, timezone?: string): Date {
  const tz = timezone ?? APP_DEFAULTS.TIMEZONE;

  const formatter = new Intl.DateTimeFormat('en-US', {
    timeZone: tz,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  });

  const parts = formatter.formatToParts(date);
  const hour = getDatePart(parts, 'hour');

  return new Date(
    getDatePart(parts, 'year'),
    getDatePart(parts, 'month') - 1,
    getDatePart(parts, 'day'),
    hour === 24 ? 0 : hour,
    getDatePart(parts, 'minute'),
    getDatePart(parts, 'second'),
    date.getMilliseconds()
  );
}

/**
 * Converts an app-timezone Date back to UTC.
 *
 * Replaces `DateTimeExtensions.ConvertAppDateToUtc` (lines 39-60).
 * The monolith used `TimeZoneInfo.ConvertTimeToUtc`; this implementation
 * computes the UTC offset from Intl and adjusts accordingly.
 *
 * @param date     - A Date whose local-time components represent app-timezone time.
 * @param timezone - IANA timezone identifier. Defaults to APP_DEFAULTS.TIMEZONE.
 * @returns A new Date representing the equivalent UTC instant.
 */
export function convertAppDateToUtc(date: Date, timezone?: string): Date {
  const tz = timezone ?? APP_DEFAULTS.TIMEZONE;

  // Step 1: Treat the date's components as a UTC timestamp (reference point)
  const utcRef = new Date(
    Date.UTC(
      date.getFullYear(),
      date.getMonth(),
      date.getDate(),
      date.getHours(),
      date.getMinutes(),
      date.getSeconds(),
      date.getMilliseconds()
    )
  );

  // Step 2: Find the timezone offset by checking what the timezone shows for utcRef
  const formatter = new Intl.DateTimeFormat('en-US', {
    timeZone: tz,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  });

  const parts = formatter.formatToParts(utcRef);
  const hour = getDatePart(parts, 'hour');
  const tzTime = Date.UTC(
    getDatePart(parts, 'year'),
    getDatePart(parts, 'month') - 1,
    getDatePart(parts, 'day'),
    hour === 24 ? 0 : hour,
    getDatePart(parts, 'minute'),
    getDatePart(parts, 'second')
  );

  // Step 3: offsetMs = how far ahead the timezone is from UTC
  const offsetMs = tzTime - utcRef.getTime();

  // Step 4: Subtract offset to get true UTC time
  return new Date(utcRef.getTime() - offsetMs);
}

/**
 * Returns a human-readable relative time string (e.g. '2 hours ago', 'yesterday').
 *
 * Uses `Intl.RelativeTimeFormat` for locale-aware formatting. Automatically
 * selects the most appropriate unit (seconds → minutes → hours → days →
 * months → years) based on the time difference.
 *
 * @param date - The date to express relative to now.
 * @returns A locale-formatted relative time string, or empty string if invalid.
 */
export function formatRelativeTime(date: Date | string): string {
  const d = parseDate(date);
  if (d === null) {
    return '';
  }

  const nowMs = Date.now();
  const diffSec = Math.round((d.getTime() - nowMs) / 1000);

  const rtf = new Intl.RelativeTimeFormat(APP_DEFAULTS.LOCALE, {
    numeric: 'auto',
  });

  // Unit thresholds: [maxAbsSeconds, unit, divisor]
  const thresholds: [number, Intl.RelativeTimeFormatUnit, number][] = [
    [60, 'second', 1],
    [3600, 'minute', 60],
    [86400, 'hour', 3600],
    [2592000, 'day', 86400],
    [31536000, 'month', 2592000],
    [Infinity, 'year', 31536000],
  ];

  const absDiff = Math.abs(diffSec);
  for (const [threshold, unit, divisor] of thresholds) {
    if (absDiff < threshold) {
      return rtf.format(Math.round(diffSec / divisor), unit);
    }
  }

  return rtf.format(Math.round(diffSec / 31536000), 'year');
}

// =============================================================================
// Phase 2: Number Formatting
// =============================================================================

/**
 * Formats a numeric value with locale-aware thousands separators and
 * decimal precision.
 *
 * @param value         - The numeric value to format.
 * @param decimalPlaces - Maximum fractional digits. Defaults to undefined (locale default).
 * @param locale        - BCP 47 locale tag. Defaults to APP_DEFAULTS.LOCALE.
 * @returns The formatted number string, or empty string if value is null/undefined.
 */
export function formatNumber(
  value: number | null,
  decimalPlaces?: number,
  locale?: string
): string {
  if (value == null) {
    return '';
  }

  const loc = locale ?? APP_DEFAULTS.LOCALE;
  const options: Intl.NumberFormatOptions = {};

  if (decimalPlaces !== undefined) {
    options.minimumFractionDigits = decimalPlaces;
    options.maximumFractionDigits = decimalPlaces;
  }

  return new Intl.NumberFormat(loc, options).format(value);
}

/**
 * Formats a numeric value as a percentage string.
 *
 * Input values are in decimal form: 0.75 → '75%'. Uses `Intl.NumberFormat`
 * with `style: 'percent'` for locale-aware formatting.
 *
 * @param value         - The decimal value to format (0.75 = 75%).
 * @param decimalPlaces - Maximum fractional digits for the percentage.
 * @param locale        - BCP 47 locale tag. Defaults to APP_DEFAULTS.LOCALE.
 * @returns The formatted percentage string, or empty string if null/undefined.
 */
export function formatPercent(
  value: number | null,
  decimalPlaces?: number,
  locale?: string
): string {
  if (value == null) {
    return '';
  }

  const loc = locale ?? APP_DEFAULTS.LOCALE;
  const options: Intl.NumberFormatOptions = {
    style: 'percent',
  };

  if (decimalPlaces !== undefined) {
    options.minimumFractionDigits = decimalPlaces;
    options.maximumFractionDigits = decimalPlaces;
  }

  return new Intl.NumberFormat(loc, options).format(value);
}

// =============================================================================
// Phase 3: Currency Formatting (from Definitions.cs CurrencyType + Helpers.cs)
// =============================================================================

/**
 * Formats a numeric value as a currency string.
 *
 * Respects the `CurrencyType.symbolPlacement` (Before/After) and
 * `CurrencyType.decimalDigits` properties from the COMMON_CURRENCIES
 * catalog in constants.ts, mirroring the monolith's currency formatting
 * from Definitions.cs (lines 64-90) and Helpers.cs.
 *
 * If the currency code is found in COMMON_CURRENCIES, custom formatting
 * with native symbol and correct placement is used. Otherwise, falls back
 * to `Intl.NumberFormat` with `style: 'currency'`.
 *
 * @param value        - The numeric value to format.
 * @param currencyCode - ISO 4217 currency code (e.g. 'USD', 'EUR').
 *                       Defaults to APP_DEFAULTS.CURRENCY_CODE.
 * @param locale       - BCP 47 locale tag. Defaults to APP_DEFAULTS.LOCALE.
 * @returns The formatted currency string, or empty string if null/undefined.
 */
export function formatCurrency(
  value: number | null,
  currencyCode?: string,
  locale?: string
): string {
  if (value == null) {
    return '';
  }

  const code = currencyCode ?? APP_DEFAULTS.CURRENCY_CODE;
  const loc = locale ?? APP_DEFAULTS.LOCALE;
  const currency: CurrencyType | undefined =
    COMMON_CURRENCIES[code.toUpperCase()];

  if (currency) {
    // Custom formatting honouring CurrencyType properties
    const formatted = new Intl.NumberFormat(loc, {
      minimumFractionDigits: currency.decimalDigits,
      maximumFractionDigits: currency.decimalDigits,
    }).format(Math.abs(value));

    const sign = value < 0 ? '-' : '';
    const symbol = currency.symbolNative || currency.symbol;

    if (currency.symbolPlacement === CurrencySymbolPlacement.Before) {
      return `${sign}${symbol}${formatted}`;
    }
    // CurrencySymbolPlacement.After is the default placement
    if (
      currency.symbolPlacement === CurrencySymbolPlacement.After ||
      currency.symbolPlacement == null
    ) {
      return `${sign}${formatted}${symbol}`;
    }
    return `${sign}${symbol}${formatted}`;
  }

  // Fallback: use Intl currency formatting for unknown codes
  try {
    return new Intl.NumberFormat(loc, {
      style: 'currency',
      currency: code,
    }).format(value);
  } catch {
    // Invalid currency code — format number with code suffix
    return `${value.toFixed(2)} ${code}`;
  }
}

/**
 * Returns the native currency symbol for a given ISO 4217 currency code.
 *
 * Looks up the code in the COMMON_CURRENCIES catalog. Returns the
 * `symbolNative` property (preferred) or the `symbol` property. If the
 * code is not found, returns the code itself as a fallback.
 *
 * @param currencyCode - ISO 4217 currency code (e.g. 'USD').
 * @returns The currency symbol string.
 */
export function getCurrencySymbol(currencyCode: string): string {
  if (!currencyCode) {
    return '';
  }

  const currency: CurrencyType | undefined =
    COMMON_CURRENCIES[currencyCode.toUpperCase()];

  if (currency) {
    return currency.symbolNative || currency.symbol;
  }

  // Fallback: attempt to extract symbol from Intl
  try {
    const parts = new Intl.NumberFormat(APP_DEFAULTS.LOCALE, {
      style: 'currency',
      currency: currencyCode,
      currencyDisplay: 'narrowSymbol',
    }).formatToParts(0);

    const symbolPart = parts.find((p) => p.type === 'currency');
    return symbolPart?.value ?? currencyCode;
  } catch {
    return currencyCode;
  }
}

// =============================================================================
// Phase 4: Template Token Replacement (from RenderService.cs lines 126-357)
// =============================================================================

/**
 * Renders a template string by replacing `{{…}}` tokens with resolved values.
 *
 * Replicates the monolith's `RenderService.RenderHtmlWithTemplate`
 * (RenderService.cs line 126) token replacement logic:
 *
 * - `{{Record["fieldName"]}}` — resolves from the `data` parameter
 * - `{{ErpRequestContext.path}}` — resolves nested property from `context`
 * - `{{ErpAppContext.path}}` — resolves nested property from `context`
 * - `{{ListMeta.path}}` — resolves nested property from `context`
 * - `{{ViewMeta.path}}` — resolves nested property from `context`
 * - `{{key ?? "default"}}` — uses the default value when the key is unresolved
 * - `{{anyKey}}` — resolves from `data` first, then `context`
 *
 * @param template - The template string with `{{…}}` tokens.
 * @param data     - Record/entity data for field value resolution.
 * @param context  - Application context for nested property resolution.
 * @returns The interpolated string with all tokens replaced.
 */
export function renderTemplate(
  template: string,
  data?: Record<string, unknown>,
  context?: Record<string, unknown>
): string {
  if (!template) {
    return '';
  }

  // Match all {{…}} token patterns — the regex captures the content between {{ and }}
  const tokenPattern = /\{\{([^}]*)\}\}/g;
  let match: RegExpExecArray | null;
  let result = template;

  // Collect distinct tokens to avoid redundant processing
  const tokens = new Set<string>();
  while ((match = tokenPattern.exec(template)) !== null) {
    tokens.add(match[1]);
  }

  for (const tag of tokens) {
    const resolved = resolveTemplateToken(tag, data, context);
    // Replace all occurrences of this token in the result
    result = result.split(`{{${tag}}}`).join(resolved);
  }

  return result;
}

/**
 * Resolves a single template token to its string value.
 *
 * Implements the token resolution logic from `RenderService.RenderHtmlWithTemplate`
 * (RenderService.cs lines 128-284):
 *
 * 1. Strips extraneous `{{` / `}}` characters
 * 2. Extracts `?? "default"` fallback values
 * 3. Resolves by prefix:
 *    - `Record["field"]` → data lookup (case-insensitive field name)
 *    - `ErpRequestContext.` → getNestedProperty on context
 *    - `ErpAppContext.` → getNestedProperty on context
 *    - `ListMeta.` → getNestedProperty on context
 *    - `ViewMeta.` → getNestedProperty on context
 *    - Generic key → data lookup, then context lookup
 * 4. Returns the default value when resolution yields null/undefined
 *
 * @param token   - The raw token content (without surrounding `{{` / `}}`).
 * @param data    - Record/entity data for field value resolution.
 * @param context - Application context for nested property resolution.
 * @returns The resolved string value.
 */
export function resolveTemplateToken(
  token: string,
  data?: Record<string, unknown>,
  context?: Record<string, unknown>
): string {
  if (!token) {
    return '';
  }

  // Clean any residual braces and trim whitespace
  let processedTag = token.replace(/\{\{/g, '').replace(/\}\}/g, '').trim();
  let defaultValue = '';

  // Extract ?? default value syntax: {{fieldName ?? "default"}}
  if (processedTag.includes('??')) {
    const questionIdx = processedTag.indexOf('??');
    const tagValue = processedTag.substring(0, questionIdx).trim();
    const tagDefault = processedTag
      .substring(questionIdx + 2)
      .trim()
      .replace(/"/g, '')
      .replace(/'/g, '');
    processedTag = tagValue;
    defaultValue = tagDefault;
  }

  // Record["fieldName"] pattern — matches RenderService.cs lines 143-154
  if (processedTag.startsWith('Record[') && data != null) {
    const fieldName = processedTag
      .replace('Record["', '')
      .replace('"]', '')
      .toLowerCase();

    if (fieldName in data && data[fieldName] != null) {
      return String(data[fieldName]);
    }

    // Also try case-insensitive lookup
    const lowerData = Object.keys(data).find(
      (k) => k.toLowerCase() === fieldName
    );
    if (lowerData && data[lowerData] != null) {
      return String(data[lowerData]);
    }

    return defaultValue;
  }

  // ErpRequestContext.Property.Path — lines 156-167
  if (processedTag.startsWith('ErpRequestContext.') && context != null) {
    const propertyPath = processedTag.replace('ErpRequestContext.', '');
    const value = getNestedProperty(context, propertyPath);
    return value != null ? String(value) : defaultValue;
  }

  // ErpAppContext.Property.Path — lines 170-182
  if (processedTag.startsWith('ErpAppContext.') && context != null) {
    const propertyPath = processedTag.replace('ErpAppContext.', '');
    const value = getNestedProperty(context, propertyPath);
    return value != null ? String(value) : defaultValue;
  }

  // ListMeta.Property.Path — lines 184-196
  if (processedTag.startsWith('ListMeta.') && context != null) {
    const propertyPath = processedTag.replace('ListMeta.', '');
    const value = getNestedProperty(context, propertyPath);
    return value != null ? String(value) : defaultValue;
  }

  // ViewMeta.Property.Path — lines 197-208
  if (processedTag.startsWith('ViewMeta.') && context != null) {
    const propertyPath = processedTag.replace('ViewMeta.', '');
    const value = getNestedProperty(context, propertyPath);
    return value != null ? String(value) : defaultValue;
  }

  // erp-allow-roles / erp-block-roles / erp-authorize — lines 211-268
  // These are authorization directives handled at the component level in the
  // React SPA (via AuthContext / route guards), so they resolve to empty string
  // when encountered in template rendering.
  if (
    processedTag.startsWith('erp-allow-roles') ||
    processedTag.startsWith('erp-block-roles') ||
    processedTag === 'erp-authorize'
  ) {
    return '';
  }

  // CurrentUrlEncoded — line 270
  if (processedTag === 'CurrentUrlEncoded') {
    if (typeof window !== 'undefined') {
      return encodeURIComponent(
        window.location.pathname + window.location.search
      );
    }
    return defaultValue;
  }

  // Generic key resolution: try data first, then context
  if (data != null && processedTag in data && data[processedTag] != null) {
    return String(data[processedTag]);
  }

  // Case-insensitive lookup in data
  if (data != null) {
    const lowerKey = Object.keys(data).find(
      (k) => k.toLowerCase() === processedTag.toLowerCase()
    );
    if (lowerKey && data[lowerKey] != null) {
      return String(data[lowerKey]);
    }
  }

  // Try nested property access on context
  if (context != null) {
    const value = getNestedProperty(context, processedTag);
    if (value != null) {
      return String(value);
    }
  }

  return defaultValue;
}

// =============================================================================
// Phase 5: File Type Icon Mapping (from RenderService.cs lines 22-124)
// =============================================================================

/**
 * Extension-to-icon category mapping.
 *
 * Derived from `RenderService.GetPathTypeIcon` (lines 22-124), converted
 * from Font Awesome class names to Lucide icon identifiers.
 *
 * | FA Class            | Lucide Icon        | Category     |
 * |---------------------|--------------------|--------------|
 * | fa-file-alt         | file-text          | text         |
 * | fa-file-pdf         | file-type          | pdf          |
 * | fa-file-word        | file-type-2        | word         |
 * | fa-file-excel       | file-spreadsheet   | excel        |
 * | fa-file-powerpoint  | presentation       | powerpoint   |
 * | fa-file-image       | file-image         | image        |
 * | fa-file-archive     | file-archive       | archive      |
 * | fa-file-audio       | file-audio         | audio        |
 * | fa-file-video       | file-video         | video        |
 * | fa-file-code        | file-code          | code         |
 * | fa-cogs             | file-cog           | executable   |
 * | fa-globe            | globe              | web          |
 * | fa-file             | file               | default      |
 */
const FILE_EXTENSION_MAP: Record<string, string> = {
  // Text
  '.txt': 'file-text',

  // PDF
  '.pdf': 'file-type',

  // Word processing
  '.doc': 'file-type-2',
  '.docx': 'file-type-2',

  // Spreadsheets
  '.xls': 'file-spreadsheet',
  '.xlsx': 'file-spreadsheet',

  // Presentations
  '.ppt': 'presentation',
  '.pptx': 'presentation',

  // Images
  '.gif': 'file-image',
  '.jpg': 'file-image',
  '.jpeg': 'file-image',
  '.png': 'file-image',
  '.bmp': 'file-image',
  '.tif': 'file-image',
  '.svg': 'file-image',
  '.webp': 'file-image',

  // Archives
  '.zip': 'file-archive',
  '.zipx': 'file-archive',
  '.rar': 'file-archive',
  '.tar': 'file-archive',
  '.gz': 'file-archive',
  '.dmg': 'file-archive',
  '.iso': 'file-archive',
  '.7z': 'file-archive',

  // Audio
  '.wav': 'file-audio',
  '.mp3': 'file-audio',
  '.fla': 'file-audio',
  '.flac': 'file-audio',
  '.ra': 'file-audio',
  '.rma': 'file-audio',
  '.aif': 'file-audio',
  '.aiff': 'file-audio',
  '.aa': 'file-audio',
  '.aac': 'file-audio',
  '.aax': 'file-audio',
  '.ac3': 'file-audio',
  '.au': 'file-audio',
  '.ogg': 'file-audio',
  '.avr': 'file-audio',
  '.3ga': 'file-audio',
  '.mid': 'file-audio',
  '.midi': 'file-audio',
  '.m4a': 'file-audio',
  '.mp4a': 'file-audio',
  '.amz': 'file-audio',
  '.mka': 'file-audio',
  '.asx': 'file-audio',
  '.pcm': 'file-audio',
  '.m3u': 'file-audio',
  '.wma': 'file-audio',
  '.xwma': 'file-audio',

  // Video
  '.avi': 'file-video',
  '.mpg': 'file-video',
  '.mp4': 'file-video',
  '.mkv': 'file-video',
  '.mov': 'file-video',
  '.wmv': 'file-video',
  '.vp6': 'file-video',
  '.264': 'file-video',
  '.vid': 'file-video',
  '.rv': 'file-video',
  '.webm': 'file-video',
  '.swf': 'file-video',
  '.h264': 'file-video',
  '.flv': 'file-video',
  '.mk3d': 'file-video',
  '.gifv': 'file-video',
  '.oggv': 'file-video',
  '.3gp': 'file-video',
  '.m4v': 'file-video',
  '.movie': 'file-video',
  '.divx': 'file-video',

  // Code (from RenderService.cs lines 89-97)
  '.c': 'file-code',
  '.cpp': 'file-code',
  '.css': 'file-code',
  '.js': 'file-code',
  '.ts': 'file-code',
  '.tsx': 'file-code',
  '.jsx': 'file-code',
  '.py': 'file-code',
  '.git': 'file-code',
  '.cs': 'file-code',
  '.cshtml': 'file-code',
  '.xml': 'file-code',
  '.ini': 'file-code',
  '.config': 'file-code',
  '.json': 'file-code',
  '.h': 'file-code',
  '.yaml': 'file-code',
  '.yml': 'file-code',

  // Executables / scripts (from RenderService.cs lines 99-105)
  '.exe': 'file-cog',
  '.jar': 'file-cog',
  '.dll': 'file-cog',
  '.bat': 'file-cog',
  '.pl': 'file-cog',
  '.scr': 'file-cog',
  '.msi': 'file-cog',
  '.app': 'file-cog',
  '.deb': 'file-cog',
  '.apk': 'file-cog',
  '.vb': 'file-cog',
  '.prg': 'file-cog',
  '.sh': 'file-cog',

  // Web (from RenderService.cs lines 109-119)
  '.htm': 'globe',
  '.html': 'globe',
  '.xhtml': 'globe',
  '.jhtml': 'globe',
  '.php': 'globe',
  '.php3': 'globe',
  '.php4': 'globe',
  '.php5': 'globe',
  '.phtml': 'globe',
  '.asp': 'globe',
  '.aspx': 'globe',
};

/**
 * Returns a Lucide icon name for a given file path based on its extension.
 *
 * Replaces `RenderService.GetPathTypeIcon` (lines 22-124). Icon names use
 * Lucide naming convention (kebab-case) instead of the monolith's Font
 * Awesome class names.
 *
 * @param filePath - The file path or file name to classify.
 * @returns A Lucide icon name string (e.g. 'file-text', 'file-image').
 */
export function getFileTypeIcon(filePath: string): string {
  if (!filePath) {
    return 'file';
  }

  const lowerPath = filePath.toLowerCase();

  // Check explicit extension matches
  for (const [ext, icon] of Object.entries(FILE_EXTENSION_MAP)) {
    if (lowerPath.endsWith(ext)) {
      return icon;
    }
  }

  // Web URL heuristics (from RenderService.cs lines 109-119)
  if (
    lowerPath.endsWith('.com') ||
    lowerPath.endsWith('.net') ||
    lowerPath.endsWith('.org') ||
    lowerPath.endsWith('.edu') ||
    lowerPath.endsWith('.gov') ||
    lowerPath.endsWith('.mil') ||
    lowerPath.endsWith('/') ||
    lowerPath.endsWith('?') ||
    lowerPath.endsWith('#')
  ) {
    return 'globe';
  }

  return 'file';
}

// =============================================================================
// Phase 6: HTML Snippet Extraction (from RenderService.cs lines 359-386)
// =============================================================================

/**
 * Extracts a plain-text snippet from an HTML string.
 *
 * Replaces `RenderService.GetSnippetFromHtml` (lines 359-386):
 * 1. Parses the HTML into a DOM tree via `DOMParser` in the browser.
 * 2. Walks all leaf nodes and collects their trimmed text content.
 * 3. Joins fragments with newlines.
 * 4. Truncates to `maxLength` and appends `'...'` when the result exceeds it.
 *
 * Falls back to regex-based tag stripping when `DOMParser` is unavailable
 * (e.g. SSR or test environments).
 *
 * @param html      - The HTML string to extract text from.
 * @param maxLength - Maximum character count before truncation.
 *                    Defaults to APP_DEFAULTS.SNIPPET_MAX_LENGTH (150).
 * @returns The extracted plain-text snippet.
 */
export function extractSnippetText(
  html: string,
  maxLength?: number
): string {
  const limit = maxLength ?? APP_DEFAULTS.SNIPPET_MAX_LENGTH;

  if (!html || !html.trim()) {
    return '';
  }

  let textContent = '';

  if (typeof DOMParser !== 'undefined') {
    try {
      const parser = new DOMParser();
      const doc = parser.parseFromString(html, 'text/html');
      const parts: string[] = [];

      /**
       * Recursively collects trimmed text from leaf nodes — mirrors the C#
       * `root.DescendantsAndSelf()` traversal that checks `!node.HasChildNodes`.
       */
      const collectLeafText = (node: Node): void => {
        if (node.childNodes.length === 0) {
          const text = (node.textContent || '').trim();
          if (text) {
            parts.push(text);
          }
        } else {
          for (let i = 0; i < node.childNodes.length; i++) {
            collectLeafText(node.childNodes[i]);
          }
        }
      };

      collectLeafText(doc.body);
      textContent = parts.join('\n');
    } catch {
      // DOMParser failed — fall through to regex path
      textContent = stripHtmlTags(html);
    }
  } else {
    textContent = stripHtmlTags(html);
  }

  if (textContent.length > limit) {
    return textContent.substring(0, limit) + '...';
  }

  return textContent;
}

/**
 * Strips HTML tags using regex and normalises whitespace.
 * Used as a fallback when `DOMParser` is not available.
 */
function stripHtmlTags(html: string): string {
  return html
    .replace(/<[^>]*>/g, '\n')
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
    .join('\n');
}
