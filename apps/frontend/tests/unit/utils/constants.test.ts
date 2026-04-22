/**
 * Vitest Tests for System Constants and ID Validation
 *
 * Validates that system constants, enum values, and ID mappings defined in
 * `apps/frontend/src/utils/constants.ts` are correctly defined, preserving
 * exact values from the monolith's source files:
 *   - WebVella.Erp/Api/Definitions.cs        (SystemIds, EntityPermission, FilterOperatorTypes)
 *   - WebVella.Erp/Api/Models/FieldTypes/FieldType.cs (21 field types, labels)
 *   - WebVella.Erp/Utilities/Helpers.cs       (currency catalog)
 *
 * Rules:
 *   - Pure value assertions only — no DOM rendering, no React, no mocking
 *   - All GUIDs are validated as lowercase versions of the C# originals
 *   - Named imports only — no default exports in the source module
 */

import { describe, it, expect } from 'vitest';
import {
  // SystemIds
  SYSTEM_USER_ID,
  ADMINISTRATOR_ROLE_ID,
  REGULAR_ROLE_ID,
  GUEST_ROLE_ID,
  SYSTEM_ENTITY_ID,
  USER_ENTITY_ID,
  ROLE_ENTITY_ID,
  FIRST_USER_ID,
  EMPTY_GUID,
  AREA_ENTITY_ID,
  USER_ROLE_RELATION_ID,
  // Enums
  EntityPermission,
  FieldType,
  FieldTypeValues,
  FieldTypeLabels,
  FilterOperatorTypes,
  // Currency
  COMMON_CURRENCIES,
  // API & Defaults
  API_VERSION,
  APP_DEFAULTS,
} from '../../../src/utils/constants';

// =============================================================================
// Helpers
// =============================================================================

/** Regex pattern for validating lowercase UUID v4-style strings (8-4-4-4-12) */
const UUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

// =============================================================================
// Phase 1: SystemIds Constants Validation
// =============================================================================

describe('SystemIds Constants', () => {
  describe('exact GUID values from Definitions.cs', () => {
    it('SYSTEM_USER_ID matches Definitions.cs line 19 — 10000000-0000-0000-0000-000000000000', () => {
      expect(SYSTEM_USER_ID).toBe('10000000-0000-0000-0000-000000000000');
    });

    it('ADMINISTRATOR_ROLE_ID matches Definitions.cs line 15 — BDC56420-... lowercased', () => {
      expect(ADMINISTRATOR_ROLE_ID).toBe('bdc56420-caf0-4030-8a0e-d264938e0cda');
    });

    it('REGULAR_ROLE_ID matches Definitions.cs line 16 — F16EC6DB-... lowercased', () => {
      expect(REGULAR_ROLE_ID).toBe('f16ec6db-626d-4c27-8de0-3e7ce542c55f');
    });

    it('GUEST_ROLE_ID matches Definitions.cs line 17 — 987148B1-... lowercased', () => {
      expect(GUEST_ROLE_ID).toBe('987148b1-afa8-4b33-8616-55861e5fd065');
    });

    it('SYSTEM_ENTITY_ID matches Definitions.cs line 8 — a5050ac8-...', () => {
      expect(SYSTEM_ENTITY_ID).toBe('a5050ac8-5967-4ce1-95e7-a79b054f9d14');
    });

    it('USER_ENTITY_ID matches Definitions.cs line 9 — b9cebc3b-...', () => {
      expect(USER_ENTITY_ID).toBe('b9cebc3b-6443-452a-8e34-b311a73dcc8b');
    });

    it('ROLE_ENTITY_ID matches Definitions.cs line 10 — c4541fee-...', () => {
      expect(ROLE_ENTITY_ID).toBe('c4541fee-fbb6-4661-929e-1724adec285a');
    });

    it('AREA_ENTITY_ID matches Definitions.cs line 11 — cb434298-...', () => {
      expect(AREA_ENTITY_ID).toBe('cb434298-8583-4a96-bdbb-97b2c1764192');
    });

    it('USER_ROLE_RELATION_ID matches Definitions.cs line 13 — 0C4B119E-... lowercased', () => {
      expect(USER_ROLE_RELATION_ID).toBe('0c4b119e-1d7b-4b40-8d2c-9e447cc656ab');
    });

    it('FIRST_USER_ID matches Definitions.cs line 20 — EABD66FD-... lowercased', () => {
      expect(FIRST_USER_ID).toBe('eabd66fd-8de1-4d79-9674-447ee89921c2');
    });

    it('EMPTY_GUID is the zero UUID', () => {
      expect(EMPTY_GUID).toBe('00000000-0000-0000-0000-000000000000');
    });
  });

  describe('UUID format validation', () => {
    const systemIds: Record<string, string> = {
      SYSTEM_USER_ID,
      ADMINISTRATOR_ROLE_ID,
      REGULAR_ROLE_ID,
      GUEST_ROLE_ID,
      SYSTEM_ENTITY_ID,
      USER_ENTITY_ID,
      ROLE_ENTITY_ID,
      AREA_ENTITY_ID,
      USER_ROLE_RELATION_ID,
      FIRST_USER_ID,
      EMPTY_GUID,
    };

    it('all SystemIds match the lowercase UUID format (8-4-4-4-12 hex)', () => {
      for (const [name, value] of Object.entries(systemIds)) {
        expect(value, `${name} should match UUID regex`).toMatch(UUID_REGEX);
      }
    });

    it('no SystemId contains uppercase letters', () => {
      for (const [name, value] of Object.entries(systemIds)) {
        expect(value, `${name} should be lowercase`).toBe(value.toLowerCase());
      }
    });
  });

  describe('uniqueness', () => {
    it('all SystemIds are unique values', () => {
      const ids = [
        SYSTEM_USER_ID,
        ADMINISTRATOR_ROLE_ID,
        REGULAR_ROLE_ID,
        GUEST_ROLE_ID,
        SYSTEM_ENTITY_ID,
        USER_ENTITY_ID,
        ROLE_ENTITY_ID,
        AREA_ENTITY_ID,
        USER_ROLE_RELATION_ID,
        FIRST_USER_ID,
        EMPTY_GUID,
      ];
      const uniqueIds = new Set(ids);
      expect(uniqueIds.size).toBe(ids.length);
    });
  });
});

// =============================================================================
// Phase 2: EntityPermission Enum Validation
// =============================================================================

describe('EntityPermission', () => {
  it('has Read permission defined', () => {
    expect(EntityPermission.Read).toBeDefined();
  });

  it('has Create permission defined', () => {
    expect(EntityPermission.Create).toBeDefined();
  });

  it('has Update permission defined', () => {
    expect(EntityPermission.Update).toBeDefined();
  });

  it('has Delete permission defined', () => {
    expect(EntityPermission.Delete).toBeDefined();
  });

  it('contains exactly 4 permission entries (Read, Create, Update, Delete)', () => {
    const keys = Object.keys(EntityPermission);
    expect(keys).toHaveLength(4);
    expect(keys).toContain('Read');
    expect(keys).toContain('Create');
    expect(keys).toContain('Update');
    expect(keys).toContain('Delete');
  });

  it('all permission values are distinct', () => {
    const values = Object.values(EntityPermission);
    const uniqueValues = new Set(values);
    expect(uniqueValues.size).toBe(values.length);
  });

  it('Read maps to "read"', () => {
    expect(EntityPermission.Read).toBe('read');
  });

  it('Create maps to "create"', () => {
    expect(EntityPermission.Create).toBe('create');
  });

  it('Update maps to "update"', () => {
    expect(EntityPermission.Update).toBe('update');
  });

  it('Delete maps to "delete"', () => {
    expect(EntityPermission.Delete).toBe('delete');
  });
});

// =============================================================================
// Phase 3: FieldType String Keys Validation
// =============================================================================

describe('FieldType (string keys)', () => {
  it('contains exactly 21 field type entries matching FieldType.cs', () => {
    const keys = Object.keys(FieldType);
    expect(keys).toHaveLength(21);
  });

  it('AutoNumberField key exists with value "AutoNumberField"', () => {
    expect(FieldType.AutoNumberField).toBe('AutoNumberField');
  });

  it('CheckboxField key exists with value "CheckboxField"', () => {
    expect(FieldType.CheckboxField).toBe('CheckboxField');
  });

  it('CurrencyField key exists with value "CurrencyField"', () => {
    expect(FieldType.CurrencyField).toBe('CurrencyField');
  });

  it('DateField key exists with value "DateField"', () => {
    expect(FieldType.DateField).toBe('DateField');
  });

  it('DateTimeField key exists with value "DateTimeField"', () => {
    expect(FieldType.DateTimeField).toBe('DateTimeField');
  });

  it('EmailField key exists with value "EmailField"', () => {
    expect(FieldType.EmailField).toBe('EmailField');
  });

  it('FileField key exists with value "FileField"', () => {
    expect(FieldType.FileField).toBe('FileField');
  });

  it('HtmlField key exists with value "HtmlField"', () => {
    expect(FieldType.HtmlField).toBe('HtmlField');
  });

  it('ImageField key exists with value "ImageField"', () => {
    expect(FieldType.ImageField).toBe('ImageField');
  });

  it('MultiLineTextField key exists with value "MultiLineTextField"', () => {
    expect(FieldType.MultiLineTextField).toBe('MultiLineTextField');
  });

  it('MultiSelectField key exists with value "MultiSelectField"', () => {
    expect(FieldType.MultiSelectField).toBe('MultiSelectField');
  });

  it('NumberField key exists with value "NumberField"', () => {
    expect(FieldType.NumberField).toBe('NumberField');
  });

  it('PasswordField key exists with value "PasswordField"', () => {
    expect(FieldType.PasswordField).toBe('PasswordField');
  });

  it('PercentField key exists with value "PercentField"', () => {
    expect(FieldType.PercentField).toBe('PercentField');
  });

  it('PhoneField key exists with value "PhoneField"', () => {
    expect(FieldType.PhoneField).toBe('PhoneField');
  });

  it('GuidField key exists with value "GuidField"', () => {
    expect(FieldType.GuidField).toBe('GuidField');
  });

  it('SelectField key exists with value "SelectField"', () => {
    expect(FieldType.SelectField).toBe('SelectField');
  });

  it('TextField key exists with value "TextField"', () => {
    expect(FieldType.TextField).toBe('TextField');
  });

  it('UrlField key exists with value "UrlField"', () => {
    expect(FieldType.UrlField).toBe('UrlField');
  });

  it('RelationField key exists with value "RelationField"', () => {
    expect(FieldType.RelationField).toBe('RelationField');
  });

  it('GeographyField key exists with value "GeographyField"', () => {
    expect(FieldType.GeographyField).toBe('GeographyField');
  });

  it('all FieldType string values are unique', () => {
    const values = Object.values(FieldType);
    const uniqueValues = new Set(values);
    expect(uniqueValues.size).toBe(values.length);
  });
});

// =============================================================================
// Phase 4: FieldTypeValues Numeric Mapping Validation
// =============================================================================

describe('FieldTypeValues (numeric mapping from FieldType.cs)', () => {
  it('contains exactly 21 entries matching FieldType.cs', () => {
    const keys = Object.keys(FieldTypeValues);
    expect(keys).toHaveLength(21);
  });

  it('AutoNumberField maps to 1', () => {
    expect(FieldTypeValues.AutoNumberField).toBe(1);
  });

  it('CheckboxField maps to 2', () => {
    expect(FieldTypeValues.CheckboxField).toBe(2);
  });

  it('CurrencyField maps to 3', () => {
    expect(FieldTypeValues.CurrencyField).toBe(3);
  });

  it('DateField maps to 4', () => {
    expect(FieldTypeValues.DateField).toBe(4);
  });

  it('DateTimeField maps to 5', () => {
    expect(FieldTypeValues.DateTimeField).toBe(5);
  });

  it('EmailField maps to 6', () => {
    expect(FieldTypeValues.EmailField).toBe(6);
  });

  it('FileField maps to 7', () => {
    expect(FieldTypeValues.FileField).toBe(7);
  });

  it('HtmlField maps to 8', () => {
    expect(FieldTypeValues.HtmlField).toBe(8);
  });

  it('ImageField maps to 9', () => {
    expect(FieldTypeValues.ImageField).toBe(9);
  });

  it('MultiLineTextField maps to 10', () => {
    expect(FieldTypeValues.MultiLineTextField).toBe(10);
  });

  it('MultiSelectField maps to 11', () => {
    expect(FieldTypeValues.MultiSelectField).toBe(11);
  });

  it('NumberField maps to 12', () => {
    expect(FieldTypeValues.NumberField).toBe(12);
  });

  it('PasswordField maps to 13', () => {
    expect(FieldTypeValues.PasswordField).toBe(13);
  });

  it('PercentField maps to 14', () => {
    expect(FieldTypeValues.PercentField).toBe(14);
  });

  it('PhoneField maps to 15', () => {
    expect(FieldTypeValues.PhoneField).toBe(15);
  });

  it('GuidField maps to 16', () => {
    expect(FieldTypeValues.GuidField).toBe(16);
  });

  it('SelectField maps to 17', () => {
    expect(FieldTypeValues.SelectField).toBe(17);
  });

  it('TextField maps to 18', () => {
    expect(FieldTypeValues.TextField).toBe(18);
  });

  it('UrlField maps to 19', () => {
    expect(FieldTypeValues.UrlField).toBe(19);
  });

  it('RelationField maps to 20', () => {
    expect(FieldTypeValues.RelationField).toBe(20);
  });

  it('GeographyField maps to 21', () => {
    expect(FieldTypeValues.GeographyField).toBe(21);
  });

  it('all numeric values are unique', () => {
    const values = Object.values(FieldTypeValues);
    const uniqueValues = new Set(values);
    expect(uniqueValues.size).toBe(values.length);
  });

  it('numeric values form a contiguous range from 1 to 21', () => {
    const values = Object.values(FieldTypeValues).sort((a, b) => a - b);
    expect(values[0]).toBe(1);
    expect(values[values.length - 1]).toBe(21);
    for (let i = 0; i < values.length; i++) {
      expect(values[i]).toBe(i + 1);
    }
  });
});

// =============================================================================
// Phase 5: FieldTypeLabels Validation
// =============================================================================

describe('FieldTypeLabels (from [SelectOption(Label = "...")] attributes)', () => {
  it('contains exactly 21 label entries', () => {
    const keys = Object.keys(FieldTypeLabels);
    expect(keys).toHaveLength(21);
  });

  it('AutoNumberField label is "autonumber"', () => {
    expect(FieldTypeLabels.AutoNumberField).toBe('autonumber');
  });

  it('CheckboxField label is "checkbox"', () => {
    expect(FieldTypeLabels.CheckboxField).toBe('checkbox');
  });

  it('CurrencyField label is "currency"', () => {
    expect(FieldTypeLabels.CurrencyField).toBe('currency');
  });

  it('DateField label is "date"', () => {
    expect(FieldTypeLabels.DateField).toBe('date');
  });

  it('DateTimeField label is "datetime"', () => {
    expect(FieldTypeLabels.DateTimeField).toBe('datetime');
  });

  it('EmailField label is "email"', () => {
    expect(FieldTypeLabels.EmailField).toBe('email');
  });

  it('FileField label is "file"', () => {
    expect(FieldTypeLabels.FileField).toBe('file');
  });

  it('HtmlField label is "html"', () => {
    expect(FieldTypeLabels.HtmlField).toBe('html');
  });

  it('ImageField label is "image"', () => {
    expect(FieldTypeLabels.ImageField).toBe('image');
  });

  it('MultiLineTextField label is "multilinetext"', () => {
    expect(FieldTypeLabels.MultiLineTextField).toBe('multilinetext');
  });

  it('MultiSelectField label is "multiselect"', () => {
    expect(FieldTypeLabels.MultiSelectField).toBe('multiselect');
  });

  it('NumberField label is "number"', () => {
    expect(FieldTypeLabels.NumberField).toBe('number');
  });

  it('PasswordField label is "password"', () => {
    expect(FieldTypeLabels.PasswordField).toBe('password');
  });

  it('PercentField label is "percent"', () => {
    expect(FieldTypeLabels.PercentField).toBe('percent');
  });

  it('PhoneField label is "phone"', () => {
    expect(FieldTypeLabels.PhoneField).toBe('phone');
  });

  it('GuidField label is "guid"', () => {
    expect(FieldTypeLabels.GuidField).toBe('guid');
  });

  it('SelectField label is "select"', () => {
    expect(FieldTypeLabels.SelectField).toBe('select');
  });

  it('TextField label is "text"', () => {
    expect(FieldTypeLabels.TextField).toBe('text');
  });

  it('UrlField label is "url"', () => {
    expect(FieldTypeLabels.UrlField).toBe('url');
  });

  it('RelationField label is "relation"', () => {
    expect(FieldTypeLabels.RelationField).toBe('relation');
  });

  it('GeographyField label is "geography"', () => {
    expect(FieldTypeLabels.GeographyField).toBe('geography');
  });

  it('all labels are unique non-empty strings', () => {
    const values = Object.values(FieldTypeLabels);
    const uniqueValues = new Set(values);
    expect(uniqueValues.size).toBe(values.length);
    for (const label of values) {
      expect(label.length).toBeGreaterThan(0);
    }
  });

  it('FieldTypeLabels keys match FieldType keys exactly', () => {
    const fieldTypeKeys = Object.keys(FieldType).sort();
    const labelKeys = Object.keys(FieldTypeLabels).sort();
    expect(labelKeys).toEqual(fieldTypeKeys);
  });

  it('FieldTypeLabels keys match FieldTypeValues keys exactly', () => {
    const valuesKeys = Object.keys(FieldTypeValues).sort();
    const labelKeys = Object.keys(FieldTypeLabels).sort();
    expect(labelKeys).toEqual(valuesKeys);
  });
});

// =============================================================================
// Phase 6: FilterOperatorTypes Validation
// =============================================================================

describe('FilterOperatorTypes (Definitions.cs lines 30-44)', () => {
  it('Equals maps to 1', () => {
    expect(FilterOperatorTypes.Equals).toBe(1);
  });

  it('NotEqualTo maps to 2', () => {
    expect(FilterOperatorTypes.NotEqualTo).toBe(2);
  });

  it('StartsWith maps to 3', () => {
    expect(FilterOperatorTypes.StartsWith).toBe(3);
  });

  it('Contains maps to 4', () => {
    expect(FilterOperatorTypes.Contains).toBe(4);
  });

  it('DoesNotContain maps to 5', () => {
    expect(FilterOperatorTypes.DoesNotContain).toBe(5);
  });

  it('LessThan maps to 6', () => {
    expect(FilterOperatorTypes.LessThan).toBe(6);
  });

  it('GreaterThan maps to 7', () => {
    expect(FilterOperatorTypes.GreaterThan).toBe(7);
  });

  it('LessOrEqual maps to 8', () => {
    expect(FilterOperatorTypes.LessOrEqual).toBe(8);
  });

  it('GreaterOrEqual maps to 9', () => {
    expect(FilterOperatorTypes.GreaterOrEqual).toBe(9);
  });

  it('Includes maps to 10', () => {
    expect(FilterOperatorTypes.Includes).toBe(10);
  });

  it('Excludes maps to 11', () => {
    expect(FilterOperatorTypes.Excludes).toBe(11);
  });

  it('Within maps to 12', () => {
    expect(FilterOperatorTypes.Within).toBe(12);
  });

  it('contains exactly 12 operator entries', () => {
    const keys = Object.keys(FilterOperatorTypes);
    expect(keys).toHaveLength(12);
  });

  it('all operator values are unique', () => {
    const values = Object.values(FilterOperatorTypes);
    const uniqueValues = new Set(values);
    expect(uniqueValues.size).toBe(values.length);
  });

  it('operator values form a contiguous range from 1 to 12', () => {
    const values = Object.values(FilterOperatorTypes).sort((a, b) => a - b);
    expect(values[0]).toBe(1);
    expect(values[values.length - 1]).toBe(12);
    for (let i = 0; i < values.length; i++) {
      expect(values[i]).toBe(i + 1);
    }
  });
});

// =============================================================================
// Phase 7: COMMON_CURRENCIES Validation
// =============================================================================

describe('COMMON_CURRENCIES (from Helpers.cs currency catalog)', () => {
  describe('USD (priority 1)', () => {
    it('USD entry exists', () => {
      expect(COMMON_CURRENCIES.USD).toBeDefined();
    });

    it('USD has symbol "$"', () => {
      expect(COMMON_CURRENCIES.USD.symbol).toBe('$');
    });

    it('USD has code "USD"', () => {
      expect(COMMON_CURRENCIES.USD.code).toBe('USD');
    });

    it('USD has name "US Dollar"', () => {
      expect(COMMON_CURRENCIES.USD.name).toBe('US Dollar');
    });

    it('USD has 2 decimal digits', () => {
      expect(COMMON_CURRENCIES.USD.decimalDigits).toBe(2);
    });
  });

  describe('EUR (priority 2)', () => {
    it('EUR entry exists', () => {
      expect(COMMON_CURRENCIES.EUR).toBeDefined();
    });

    it('EUR has symbol "€"', () => {
      expect(COMMON_CURRENCIES.EUR.symbol).toBe('€');
    });

    it('EUR has code "EUR"', () => {
      expect(COMMON_CURRENCIES.EUR.code).toBe('EUR');
    });

    it('EUR has name "Euro"', () => {
      expect(COMMON_CURRENCIES.EUR.name).toBe('Euro');
    });

    it('EUR has 2 decimal digits', () => {
      expect(COMMON_CURRENCIES.EUR.decimalDigits).toBe(2);
    });
  });

  describe('GBP (priority 3)', () => {
    it('GBP entry exists', () => {
      expect(COMMON_CURRENCIES.GBP).toBeDefined();
    });

    it('GBP has symbol "£"', () => {
      expect(COMMON_CURRENCIES.GBP.symbol).toBe('£');
    });

    it('GBP has code "GBP"', () => {
      expect(COMMON_CURRENCIES.GBP.code).toBe('GBP');
    });

    it('GBP has name "British Pound"', () => {
      expect(COMMON_CURRENCIES.GBP.name).toBe('British Pound');
    });

    it('GBP has 2 decimal digits', () => {
      expect(COMMON_CURRENCIES.GBP.decimalDigits).toBe(2);
    });
  });

  describe('currency structure validation', () => {
    it('each currency has all required CurrencyType properties', () => {
      const requiredKeys: string[] = [
        'symbol',
        'symbolNative',
        'name',
        'namePlural',
        'code',
        'decimalDigits',
        'rounding',
        'symbolPlacement',
      ];

      for (const [currencyKey, currency] of Object.entries(COMMON_CURRENCIES)) {
        for (const key of requiredKeys) {
          expect(
            currency,
            `${currencyKey} should have property "${key}"`
          ).toHaveProperty(key);
        }
      }
    });

    it('all currency codes are unique', () => {
      const codes = Object.values(COMMON_CURRENCIES).map((c) => c.code);
      const uniqueCodes = new Set(codes);
      expect(uniqueCodes.size).toBe(codes.length);
    });

    it('all currency symbols are non-empty strings', () => {
      for (const [key, currency] of Object.entries(COMMON_CURRENCIES)) {
        expect(
          currency.symbol.length,
          `${key} symbol should be non-empty`
        ).toBeGreaterThan(0);
      }
    });

    it('all currency names are non-empty strings', () => {
      for (const [key, currency] of Object.entries(COMMON_CURRENCIES)) {
        expect(
          currency.name.length,
          `${key} name should be non-empty`
        ).toBeGreaterThan(0);
      }
    });

    it('all decimalDigits are non-negative integers', () => {
      for (const [key, currency] of Object.entries(COMMON_CURRENCIES)) {
        expect(
          currency.decimalDigits,
          `${key} decimalDigits should be >= 0`
        ).toBeGreaterThanOrEqual(0);
        expect(
          Number.isInteger(currency.decimalDigits),
          `${key} decimalDigits should be integer`
        ).toBe(true);
      }
    });

    it('symbolPlacement is a valid CurrencySymbolPlacement value (1=Before, 2=After)', () => {
      for (const [key, currency] of Object.entries(COMMON_CURRENCIES)) {
        expect(
          [1, 2],
          `${key} symbolPlacement should be 1 or 2`
        ).toContain(currency.symbolPlacement);
      }
    });
  });
});

// =============================================================================
// Phase 8: API_VERSION Validation
// =============================================================================

describe('API_VERSION', () => {
  it('is "v1" per AAP §0.5.1 API versioning', () => {
    expect(API_VERSION).toBe('v1');
  });

  it('is a non-empty string', () => {
    expect(typeof API_VERSION).toBe('string');
    expect(API_VERSION.length).toBeGreaterThan(0);
  });

  it('starts with "v" prefix', () => {
    expect(API_VERSION.startsWith('v')).toBe(true);
  });
});

// =============================================================================
// Phase 9: APP_DEFAULTS Validation
// =============================================================================

describe('APP_DEFAULTS', () => {
  it('PAGE_SIZE defaults to 10', () => {
    expect(APP_DEFAULTS.PAGE_SIZE).toBe(10);
  });

  it('PAGE_SIZE is a positive integer', () => {
    expect(APP_DEFAULTS.PAGE_SIZE).toBeGreaterThan(0);
    expect(Number.isInteger(APP_DEFAULTS.PAGE_SIZE)).toBe(true);
  });

  it('MAX_PAGE_SIZE is greater than PAGE_SIZE', () => {
    expect(APP_DEFAULTS.MAX_PAGE_SIZE).toBeGreaterThan(APP_DEFAULTS.PAGE_SIZE);
  });

  it('TIMEZONE defaults to "UTC"', () => {
    expect(APP_DEFAULTS.TIMEZONE).toBe('UTC');
  });

  it('LOCALE defaults to "en-US"', () => {
    expect(APP_DEFAULTS.LOCALE).toBe('en-US');
  });

  it('CURRENCY_CODE defaults to "USD"', () => {
    expect(APP_DEFAULTS.CURRENCY_CODE).toBe('USD');
  });

  it('ENTITY_NAME_MAX_LENGTH is 63 (PostgreSQL identifier limit)', () => {
    expect(APP_DEFAULTS.ENTITY_NAME_MAX_LENGTH).toBe(63);
  });

  it('SNIPPET_MAX_LENGTH is 150', () => {
    expect(APP_DEFAULTS.SNIPPET_MAX_LENGTH).toBe(150);
  });
});
