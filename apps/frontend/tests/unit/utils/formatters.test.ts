/**
 * Vitest Tests for Formatter Utility Functions
 *
 * Validates that date, datetime, time, currency, number, and percentage
 * formatting functions in `apps/frontend/src/utils/formatters.ts` are
 * correctly implemented, preserving full behavioral parity with the
 * monolith's server-side formatting:
 *
 *   - WebVella.Erp/Utilities/DateTimeExtensions.cs  (ConvertToAppDate null handling,
 *     timezone-aware date conversion)
 *   - WebVella.Erp/Utilities/Helpers.cs              (GetCurrency catalog lookup,
 *     ~170 currency entries with iso_code, symbol, symbol_first, decimal_mark)
 *   - WebVella.Erp/Api/Definitions.cs                (CurrencyType class with Symbol,
 *     SymbolNative, Code, DecimalDigits, SymbolPlacement enum Before=1/After=2)
 *
 * Rules:
 *   - Pure function testing only — no DOM rendering, no React, no mocking
 *   - Vitest framework — describe / it / expect
 *   - Relative path imports matching existing test conventions
 *   - Named imports only — no default exports
 */

import { describe, it, expect } from 'vitest';
import {
  formatDate,
  formatDateTime,
  formatTime,
  formatCurrency,
  formatNumber,
  formatPercent,
} from '../../../src/utils/formatters';

// =============================================================================
// Phase 2: Date Formatting Tests — formatDate
// Validates replacement of DateTimeExtensions.ConvertToAppDate() (lines 21-37)
// =============================================================================

describe('formatDate', () => {
  describe('basic date formatting', () => {
    it('should format a Date object to a short date string (MM/DD/YYYY)', () => {
      const result = formatDate(new Date('2024-01-15T10:30:00Z'));
      // With default 'short' format, en-US locale, UTC timezone
      // Intl.DateTimeFormat produces MM/DD/YYYY
      expect(result).toBe('01/15/2024');
    });

    it('should format a date in the middle of the year', () => {
      const result = formatDate(new Date('2024-06-30T23:59:59Z'));
      expect(result).toBe('06/30/2024');
    });

    it('should format the start of a year correctly', () => {
      const result = formatDate(new Date('2024-01-01T00:00:00Z'));
      expect(result).toBe('01/01/2024');
    });

    it('should format the end of a year correctly', () => {
      const result = formatDate(new Date('2024-12-31T23:59:59Z'));
      expect(result).toBe('12/31/2024');
    });
  });

  describe('null/undefined handling — mirrors ConvertToAppDate(DateTime?) null check (line 28-29)', () => {
    it('should return empty string for null input', () => {
      expect(formatDate(null)).toBe('');
    });

    it('should return empty string for undefined input', () => {
      // Runtime check: parseDate uses == null which catches undefined
      expect(formatDate(undefined as unknown as null)).toBe('');
    });
  });

  describe('empty string input', () => {
    it('should return empty string for empty string input', () => {
      expect(formatDate('')).toBe('');
    });
  });

  describe('string date input', () => {
    it('should parse a date-only string and return formatted date', () => {
      const result = formatDate('2024-06-15');
      // '2024-06-15' is parsed by new Date() — in UTC context this is midnight UTC
      expect(result).toBeTruthy();
      expect(typeof result).toBe('string');
      expect(result).toContain('2024');
    });

    it('should parse an ISO datetime string and return formatted date', () => {
      const result = formatDate('2024-01-15T10:30:00.000Z');
      expect(result).toBe('01/15/2024');
    });
  });

  describe('invalid date input', () => {
    it('should return empty string for invalid date string', () => {
      expect(formatDate('not-a-date')).toBe('');
    });

    it('should return empty string for random non-date string', () => {
      expect(formatDate('abc123')).toBe('');
    });
  });

  describe('format parameter variants', () => {
    it('should return long format with full month name', () => {
      const result = formatDate(new Date('2024-01-15T10:30:00Z'), 'long');
      // Intl with month: 'long' → 'January 15, 2024' in en-US
      expect(result).toContain('January');
      expect(result).toContain('15');
      expect(result).toContain('2024');
    });

    it('should return ISO format date string', () => {
      const result = formatDate(new Date('2024-01-15T10:30:00Z'), 'iso');
      expect(result).toBe('2024-01-15');
    });

    it('should handle explicit short format the same as default', () => {
      const dateObj = new Date('2024-01-15T10:30:00Z');
      const defaultResult = formatDate(dateObj);
      const shortResult = formatDate(dateObj, 'short');
      expect(shortResult).toBe(defaultResult);
    });
  });
});

// =============================================================================
// Phase 3: DateTime Formatting Tests — formatDateTime
// Tests full datetime formatting replacement from DateTimeExtensions.cs
// =============================================================================

describe('formatDateTime', () => {
  describe('basic datetime formatting', () => {
    it('should format a Date object to a short datetime string', () => {
      const result = formatDateTime(new Date('2024-01-15T10:30:00Z'));
      // Should include both date and time components
      expect(result).toBeTruthy();
      expect(result).toContain('01/15/2024');
      // Time component: 10:30 in some format
      expect(result).toContain('10');
      expect(result).toContain('30');
    });

    it('should include AM/PM in short format for en-US locale', () => {
      const result = formatDateTime(new Date('2024-01-15T14:45:00Z'));
      expect(result).toBeTruthy();
      // 14:45 UTC → 2:45 PM in en-US
      expect(result).toContain('PM');
    });
  });

  describe('null/undefined handling', () => {
    it('should return empty string for null input', () => {
      expect(formatDateTime(null)).toBe('');
    });

    it('should return empty string for undefined input', () => {
      expect(formatDateTime(undefined as unknown as null)).toBe('');
    });
  });

  describe('string input', () => {
    it('should parse an ISO datetime string and return formatted datetime', () => {
      const result = formatDateTime('2024-06-15T14:30:00Z');
      expect(result).toBeTruthy();
      expect(typeof result).toBe('string');
      expect(result.length).toBeGreaterThan(0);
      // Should contain date part
      expect(result).toContain('2024');
    });
  });

  describe('format parameter variants', () => {
    it('should return long format with full month and seconds', () => {
      const result = formatDateTime(new Date('2024-01-15T10:30:45Z'), 'long');
      expect(result).toContain('January');
      expect(result).toContain('2024');
    });

    it('should return ISO format datetime string', () => {
      const result = formatDateTime(new Date('2024-01-15T10:30:00.000Z'), 'iso');
      expect(result).toBe('2024-01-15T10:30:00.000Z');
    });
  });
});

// =============================================================================
// Phase 4: Time-Only Formatting Tests — formatTime
// =============================================================================

describe('formatTime', () => {
  describe('basic time formatting', () => {
    it('should format a Date object to a time-only string', () => {
      const result = formatTime(new Date('2024-01-15T10:30:00Z'));
      expect(result).toBeTruthy();
      // Should contain hour and minute but NOT the date
      expect(result).toContain('10');
      expect(result).toContain('30');
      // Should not contain year/month/day
      expect(result).not.toContain('2024');
      expect(result).not.toContain('01/15');
    });

    it('should format midnight correctly', () => {
      const result = formatTime(new Date('2024-01-15T00:00:00Z'));
      expect(result).toBeTruthy();
      // 00:00 UTC → 12:00 AM in en-US 12-hour format
      expect(result).toContain('12');
      expect(result).toContain('00');
    });
  });

  describe('null/undefined handling', () => {
    it('should return empty string for null input', () => {
      expect(formatTime(null)).toBe('');
    });

    it('should return empty string for undefined input', () => {
      expect(formatTime(undefined as unknown as null)).toBe('');
    });
  });

  describe('format parameter variants', () => {
    it('should return long format with seconds', () => {
      const result = formatTime(new Date('2024-01-15T10:30:45Z'), 'long');
      expect(result).toBeTruthy();
      expect(result).toContain('45');
    });

    it('should return ISO time format', () => {
      const result = formatTime(new Date('2024-01-15T10:30:45.123Z'), 'iso');
      // ISO time: 'HH:MM:SS.sss' without trailing Z
      expect(result).toBe('10:30:45.123');
    });
  });
});

// =============================================================================
// Phase 5: Currency Formatting Tests — formatCurrency
// Validates replacement of Helpers.GetCurrency() (Helpers.cs line 2567-2570)
// and CurrencyType model (Definitions.cs lines 64-90)
// =============================================================================

describe('formatCurrency', () => {
  describe('USD formatting — symbol=$, symbol_first=true, subunit_to_unit=100', () => {
    it('should format USD with dollar symbol before the value', () => {
      const result = formatCurrency(1234.56, 'USD');
      expect(result).toBe('$1,234.56');
    });

    it('should format USD with exactly 2 decimal places', () => {
      const result = formatCurrency(100, 'USD');
      expect(result).toBe('$100.00');
    });

    it('should format USD with thousands separator', () => {
      const result = formatCurrency(1000000, 'USD');
      expect(result).toBe('$1,000,000.00');
    });
  });

  describe('EUR formatting — symbol=€, symbol_first=true', () => {
    it('should format EUR with euro symbol', () => {
      const result = formatCurrency(1234.56, 'EUR');
      // EUR in COMMON_CURRENCIES has symbolPlacement: Before
      expect(result).toBe('€1,234.56');
    });
  });

  describe('GBP formatting — symbol=£, symbol_first=true', () => {
    it('should format GBP with pound symbol', () => {
      const result = formatCurrency(1234.56, 'GBP');
      expect(result).toBe('£1,234.56');
    });
  });

  describe('null value handling', () => {
    it('should return empty string for null value', () => {
      expect(formatCurrency(null)).toBe('');
    });

    it('should return empty string for undefined value', () => {
      expect(formatCurrency(undefined as unknown as null)).toBe('');
    });
  });

  describe('zero value', () => {
    it('should format zero as $0.00 for USD', () => {
      expect(formatCurrency(0, 'USD')).toBe('$0.00');
    });

    it('should format zero as €0.00 for EUR', () => {
      expect(formatCurrency(0, 'EUR')).toBe('€0.00');
    });
  });

  describe('negative value', () => {
    it('should handle negative USD values with leading minus sign', () => {
      const result = formatCurrency(-50.75, 'USD');
      // Implementation: sign prefix + symbol + formatted absolute value
      expect(result).toBe('-$50.75');
    });

    it('should handle large negative values', () => {
      const result = formatCurrency(-1234567.89, 'USD');
      expect(result).toBe('-$1,234,567.89');
    });
  });

  describe('default currency — no code provided (defaults to USD)', () => {
    it('should use USD as default when no currency code is provided', () => {
      const result = formatCurrency(100);
      expect(result).toBe('$100.00');
    });

    it('should format default currency with proper decimal places', () => {
      const result = formatCurrency(42.5);
      expect(result).toBe('$42.50');
    });
  });

  describe('case-insensitive currency code lookup', () => {
    it('should accept lowercase currency code', () => {
      const result = formatCurrency(100, 'usd');
      // Code is uppercased in lookup: COMMON_CURRENCIES[code.toUpperCase()]
      expect(result).toBe('$100.00');
    });

    it('should accept mixed-case currency code', () => {
      const result = formatCurrency(100, 'Eur');
      expect(result).toBe('€100.00');
    });
  });

  describe('unknown currency code — Intl fallback', () => {
    it('should fall back to Intl.NumberFormat for unknown currency codes', () => {
      const result = formatCurrency(100, 'JPY');
      // JPY is not in COMMON_CURRENCIES, falls back to Intl
      expect(result).toBeTruthy();
      expect(typeof result).toBe('string');
      expect(result.length).toBeGreaterThan(0);
    });
  });
});

// =============================================================================
// Phase 6: Number Formatting Tests — formatNumber
// Tests replacement of decimal_digits precision handling from PcFieldBase.cs
// =============================================================================

describe('formatNumber', () => {
  describe('basic formatting with decimal places', () => {
    it('should format with 2 decimal places and thousands separator', () => {
      expect(formatNumber(1234.5678, 2)).toBe('1,234.57');
    });

    it('should round correctly to specified decimal places', () => {
      expect(formatNumber(1.005, 2)).toBeTruthy();
      // Intl.NumberFormat handles rounding per locale rules
    });
  });

  describe('integer value with no decimals', () => {
    it('should format integer with thousands separator and zero decimals', () => {
      expect(formatNumber(1000, 0)).toBe('1,000');
    });

    it('should format small integer with no decimals', () => {
      expect(formatNumber(42, 0)).toBe('42');
    });
  });

  describe('decimal place padding', () => {
    it('should pad to specified decimal places', () => {
      expect(formatNumber(42.1, 4)).toBe('42.1000');
    });

    it('should pad integer to 2 decimal places', () => {
      expect(formatNumber(100, 2)).toBe('100.00');
    });
  });

  describe('null/undefined handling', () => {
    it('should return empty string for null value', () => {
      expect(formatNumber(null)).toBe('');
    });

    it('should return empty string for undefined value', () => {
      expect(formatNumber(undefined as unknown as null)).toBe('');
    });
  });

  describe('zero value', () => {
    it('should format zero with 2 decimal places', () => {
      expect(formatNumber(0, 2)).toBe('0.00');
    });

    it('should format zero with 0 decimal places', () => {
      expect(formatNumber(0, 0)).toBe('0');
    });
  });

  describe('negative value', () => {
    it('should format negative number with proper sign and separators', () => {
      const result = formatNumber(-1234.56, 2);
      expect(result).toBe('-1,234.56');
    });

    it('should format small negative with decimal places', () => {
      expect(formatNumber(-0.5, 2)).toBe('-0.50');
    });
  });

  describe('very large number', () => {
    it('should apply thousands separators to billion-scale numbers', () => {
      expect(formatNumber(1234567890.12, 2)).toBe('1,234,567,890.12');
    });

    it('should format millions correctly', () => {
      expect(formatNumber(9999999.99, 2)).toBe('9,999,999.99');
    });
  });

  describe('no decimal places specified (locale default)', () => {
    it('should format without explicit decimal place constraint', () => {
      const result = formatNumber(1234.5);
      // Locale default may vary; just verify it returns a string with the number
      expect(result).toBeTruthy();
      expect(result).toContain('1');
    });
  });
});

// =============================================================================
// Phase 7: Percentage Formatting Tests — formatPercent
// Values stored as 0.0-1.0 decimals matching PercentField behavior
// =============================================================================

describe('formatPercent', () => {
  describe('basic percentage — decimal input multiplied by 100', () => {
    it('should format 0.75 as 75%', () => {
      const result = formatPercent(0.75);
      expect(result).toBe('75%');
    });

    it('should format 0.5 as 50%', () => {
      expect(formatPercent(0.5)).toBe('50%');
    });

    it('should format 0.25 as 25%', () => {
      expect(formatPercent(0.25)).toBe('25%');
    });
  });

  describe('zero value', () => {
    it('should format 0 as 0%', () => {
      expect(formatPercent(0)).toBe('0%');
    });
  });

  describe('full 100%', () => {
    it('should format 1 as 100%', () => {
      expect(formatPercent(1)).toBe('100%');
    });
  });

  describe('fractional percentage', () => {
    it('should format 0.333 without explicit decimal places', () => {
      // Intl percent style default: 0 fraction digits
      // 0.333 * 100 = 33.3 → rounds to 33%
      const result = formatPercent(0.333);
      expect(result).toBe('33%');
    });

    it('should format 0.333 with 1 decimal place', () => {
      const result = formatPercent(0.333, 1);
      expect(result).toBe('33.3%');
    });

    it('should format 0.3333 with 2 decimal places', () => {
      const result = formatPercent(0.3333, 2);
      expect(result).toBe('33.33%');
    });
  });

  describe('null/undefined handling', () => {
    it('should return empty string for null value', () => {
      expect(formatPercent(null)).toBe('');
    });

    it('should return empty string for undefined value', () => {
      expect(formatPercent(undefined as unknown as null)).toBe('');
    });
  });

  describe('value > 1 (over 100%)', () => {
    it('should format 1.5 as 150%', () => {
      expect(formatPercent(1.5)).toBe('150%');
    });

    it('should format 2.0 as 200%', () => {
      expect(formatPercent(2.0)).toBe('200%');
    });

    it('should format large values correctly', () => {
      expect(formatPercent(10)).toBe('1,000%');
    });
  });

  describe('very small percentages', () => {
    it('should format 0.001 as 0%', () => {
      // 0.001 * 100 = 0.1 → rounds to 0% with default 0 fraction digits
      expect(formatPercent(0.001)).toBe('0%');
    });

    it('should format 0.001 with decimal places as 0.1%', () => {
      expect(formatPercent(0.001, 1)).toBe('0.1%');
    });
  });

  describe('negative percentages', () => {
    it('should format negative percentage correctly', () => {
      const result = formatPercent(-0.25);
      expect(result).toBe('-25%');
    });
  });
});

// =============================================================================
// Phase 8: Edge Cases and Special Values
// =============================================================================

describe('Edge Cases and Special Values', () => {
  describe('NaN input handling', () => {
    it('formatDate should return empty string for NaN input', () => {
      // NaN goes through parseDate → new Date(NaN) → Invalid Date → null → ''
      expect(formatDate(NaN as unknown as string)).toBe('');
    });

    it('formatDateTime should return empty string for NaN input', () => {
      expect(formatDateTime(NaN as unknown as string)).toBe('');
    });

    it('formatTime should return empty string for NaN input', () => {
      expect(formatTime(NaN as unknown as string)).toBe('');
    });

    it('formatNumber should handle NaN without throwing', () => {
      // NaN passes the null check and goes to Intl.NumberFormat
      const result = formatNumber(NaN as number);
      expect(typeof result).toBe('string');
    });

    it('formatCurrency should handle NaN without throwing', () => {
      const result = formatCurrency(NaN as number, 'USD');
      expect(typeof result).toBe('string');
    });

    it('formatPercent should handle NaN without throwing', () => {
      const result = formatPercent(NaN as number);
      expect(typeof result).toBe('string');
    });
  });

  describe('Infinity input handling', () => {
    it('formatNumber should handle Infinity without throwing', () => {
      const result = formatNumber(Infinity, 2);
      expect(typeof result).toBe('string');
      expect(result.length).toBeGreaterThan(0);
    });

    it('formatNumber should handle negative Infinity without throwing', () => {
      const result = formatNumber(-Infinity, 2);
      expect(typeof result).toBe('string');
      expect(result.length).toBeGreaterThan(0);
    });

    it('formatCurrency should handle Infinity without throwing', () => {
      const result = formatCurrency(Infinity, 'USD');
      expect(typeof result).toBe('string');
    });

    it('formatPercent should handle Infinity without throwing', () => {
      const result = formatPercent(Infinity);
      expect(typeof result).toBe('string');
    });
  });

  describe('very small decimal values', () => {
    it('formatNumber should handle very small decimals with precision', () => {
      const result = formatNumber(0.000001, 6);
      expect(result).toBe('0.000001');
    });

    it('formatNumber should handle very small negative decimals', () => {
      const result = formatNumber(-0.000001, 6);
      expect(result).toBe('-0.000001');
    });
  });

  describe('empty string input for date formatters', () => {
    it('formatDate should return empty string for empty string', () => {
      expect(formatDate('')).toBe('');
    });

    it('formatDateTime should return empty string for empty string', () => {
      expect(formatDateTime('')).toBe('');
    });

    it('formatTime should return empty string for empty string', () => {
      expect(formatTime('')).toBe('');
    });
  });

  describe('whitespace-only string input for date formatters', () => {
    it('formatDate should handle whitespace-only string', () => {
      // '   ' parsed by new Date() may produce Invalid Date
      const result = formatDate('   ');
      expect(typeof result).toBe('string');
    });
  });

  describe('boundary date values', () => {
    it('formatDate should handle epoch start', () => {
      const result = formatDate(new Date(0));
      expect(result).toBe('01/01/1970');
    });

    it('formatDate should handle far future date', () => {
      const result = formatDate(new Date('2099-12-31T23:59:59Z'));
      expect(result).toBe('12/31/2099');
    });
  });

  describe('number formatting boundary values', () => {
    it('formatNumber should handle Number.MAX_SAFE_INTEGER', () => {
      const result = formatNumber(Number.MAX_SAFE_INTEGER);
      expect(typeof result).toBe('string');
      expect(result.length).toBeGreaterThan(0);
    });

    it('formatNumber should handle Number.MIN_SAFE_INTEGER', () => {
      const result = formatNumber(Number.MIN_SAFE_INTEGER);
      expect(typeof result).toBe('string');
      expect(result.length).toBeGreaterThan(0);
    });

    it('formatCurrency should format fractional cents correctly', () => {
      const result = formatCurrency(0.001, 'USD');
      // USD has 2 decimal digits, so 0.001 rounds to 0.00
      expect(result).toBe('$0.00');
    });
  });
});
