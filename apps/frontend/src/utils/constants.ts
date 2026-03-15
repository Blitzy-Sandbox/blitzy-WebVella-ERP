/**
 * Application Constants — WebVella ERP Frontend
 *
 * Consolidated constants extracted from the legacy monolith's C# enums,
 * system identifiers, and configuration values. Every value is verified
 * against the original source files and lowercased where applicable.
 *
 * Sources:
 *   - WebVella.Erp/Api/Definitions.cs        (SystemIds, enums, CurrencyType)
 *   - WebVella.Erp/Api/Models/FieldTypes/FieldType.cs (21 field types)
 *   - WebVella.Erp.Web/Models/FilterType.cs   (13 filter types)
 *   - WebVella.Erp.Web/Models/PageType.cs     (7 page types)
 *   - WebVella.Erp.Web/Models/ComponentMode.cs (4 component modes)
 *   - WebVella.Erp.Web/Controllers/WebApiController.cs (API route patterns)
 *   - WebVella.Erp/Utilities/Helpers.cs       (currency catalog reference)
 *
 * Rules:
 *   - All GUIDs are lowercase for case-sensitive JS comparison
 *   - All enum-like objects use `as const` for type narrowing
 *   - Named exports only — no default export
 *   - Zero runtime dependencies — compile-time constants and types only
 *   - Tree-shakeable — each constant is independently importable
 */

// =============================================================================
// Phase 1: System Identifier GUIDs (from Definitions.cs SystemIds class)
// =============================================================================

/** System entity identifier — Definitions.cs line 8 */
export const SYSTEM_ENTITY_ID = 'a5050ac8-5967-4ce1-95e7-a79b054f9d14';

/** User entity identifier — Definitions.cs line 9 */
export const USER_ENTITY_ID = 'b9cebc3b-6443-452a-8e34-b311a73dcc8b';

/** Role entity identifier — Definitions.cs line 10 */
export const ROLE_ENTITY_ID = 'c4541fee-fbb6-4661-929e-1724adec285a';

/** Area entity identifier — Definitions.cs line 11 */
export const AREA_ENTITY_ID = 'cb434298-8583-4a96-bdbb-97b2c1764192';

/** User-to-role relation identifier — Definitions.cs line 13 (lowercased from 0C4B119E-...) */
export const USER_ROLE_RELATION_ID = '0c4b119e-1d7b-4b40-8d2c-9e447cc656ab';

/** Administrator role identifier — Definitions.cs line 15 (lowercased from BDC56420-...) */
export const ADMINISTRATOR_ROLE_ID = 'bdc56420-caf0-4030-8a0e-d264938e0cda';

/** Regular role identifier — Definitions.cs line 16 (lowercased from F16EC6DB-...) */
export const REGULAR_ROLE_ID = 'f16ec6db-626d-4c27-8de0-3e7ce542c55f';

/** Guest role identifier — Definitions.cs line 17 (lowercased from 987148B1-...) */
export const GUEST_ROLE_ID = '987148b1-afa8-4b33-8616-55861e5fd065';

/** System user identifier — Definitions.cs line 19 */
export const SYSTEM_USER_ID = '10000000-0000-0000-0000-000000000000';

/** First user identifier — Definitions.cs line 20 (lowercased from EABD66FD-...) */
export const FIRST_USER_ID = 'eabd66fd-8de1-4d79-9674-447ee89921c2';

/** Empty GUID constant for null/default comparisons */
export const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

// =============================================================================
// Phase 2: FieldType Enum (from FieldType.cs — 21 field types)
// =============================================================================

/**
 * Field type identifiers matching the C# FieldType enum.
 * Each key is the enum member name; the value is the string key used for
 * component dispatch and serialization.
 */
export const FieldType = {
  AutoNumberField: 'AutoNumberField',
  CheckboxField: 'CheckboxField',
  CurrencyField: 'CurrencyField',
  DateField: 'DateField',
  DateTimeField: 'DateTimeField',
  EmailField: 'EmailField',
  FileField: 'FileField',
  HtmlField: 'HtmlField',
  ImageField: 'ImageField',
  MultiLineTextField: 'MultiLineTextField',
  MultiSelectField: 'MultiSelectField',
  NumberField: 'NumberField',
  PasswordField: 'PasswordField',
  PercentField: 'PercentField',
  PhoneField: 'PhoneField',
  GuidField: 'GuidField',
  SelectField: 'SelectField',
  TextField: 'TextField',
  UrlField: 'UrlField',
  RelationField: 'RelationField',
  GeographyField: 'GeographyField',
} as const;

/** Union of all field type string keys */
export type FieldTypeName = keyof typeof FieldType;

/**
 * Numeric values mapping matching C# enum integer assignments.
 * AutoNumberField = 1 through GeographyField = 21.
 */
export const FieldTypeValues: Record<FieldTypeName, number> = {
  AutoNumberField: 1,
  CheckboxField: 2,
  CurrencyField: 3,
  DateField: 4,
  DateTimeField: 5,
  EmailField: 6,
  FileField: 7,
  HtmlField: 8,
  ImageField: 9,
  MultiLineTextField: 10,
  MultiSelectField: 11,
  NumberField: 12,
  PasswordField: 13,
  PercentField: 14,
  PhoneField: 15,
  GuidField: 16,
  SelectField: 17,
  TextField: 18,
  UrlField: 19,
  RelationField: 20,
  GeographyField: 21,
};

/**
 * Human-readable labels from the C# [SelectOption(Label = "...")] attributes.
 * Used in UI dropdowns and display rendering.
 */
export const FieldTypeLabels: Record<FieldTypeName, string> = {
  AutoNumberField: 'autonumber',
  CheckboxField: 'checkbox',
  CurrencyField: 'currency',
  DateField: 'date',
  DateTimeField: 'datetime',
  EmailField: 'email',
  FileField: 'file',
  HtmlField: 'html',
  ImageField: 'image',
  MultiLineTextField: 'multilinetext',
  MultiSelectField: 'multiselect',
  NumberField: 'number',
  PasswordField: 'password',
  PercentField: 'percent',
  PhoneField: 'phone',
  GuidField: 'guid',
  SelectField: 'select',
  TextField: 'text',
  UrlField: 'url',
  RelationField: 'relation',
  GeographyField: 'geography',
};

// =============================================================================
// Phase 3: FilterType Enum (from FilterType.cs — 13 filter types)
// =============================================================================

/**
 * Filter type identifiers matching the C# FilterType enum.
 * Numeric values correspond directly to the C# enum integer assignments.
 */
export const FilterType = {
  Undefined: 0,
  STARTSWITH: 1,
  CONTAINS: 2,
  EQ: 3,
  NOT: 4,
  LT: 5,
  LTE: 6,
  GT: 7,
  GTE: 8,
  REGEX: 9,
  FTS: 10,
  BETWEEN: 11,
  NOTBETWEEN: 12,
} as const;

/**
 * Human-readable labels for filter types, keyed by numeric value.
 * Derived from C# [SelectOption(Label = "...")] attributes.
 */
export const FilterTypeLabels: Record<number, string> = {
  0: 'Undefined',
  1: 'Starts with',
  2: 'Contains',
  3: 'Equals',
  4: 'Does not equal',
  5: 'Less than',
  6: 'Less than or equal to',
  7: 'Greater than',
  8: 'Greater than or equal to',
  9: 'Matches RegEx',
  10: 'Full text search',
  11: 'Between',
  12: 'Not Between',
};

// =============================================================================
// Phase 4: Other Enums (from Definitions.cs and Web/Models/)
// =============================================================================

/**
 * Entity-level permission types — Definitions.cs lines 103-109.
 * Used for role-based access control on entity CRUD operations.
 */
export const EntityPermission = {
  Read: 'read',
  Create: 'create',
  Update: 'update',
  Delete: 'delete',
} as const;

/**
 * Record list display types — Definitions.cs lines 23-28.
 * Controls how record lists are rendered in the UI.
 */
export const RecordsListTypes = {
  SearchPopup: 1,
  List: 2,
  Custom: 3,
} as const;

/**
 * Filter operator types for query building — Definitions.cs lines 30-44.
 * Each operator maps to a comparison strategy in queries.
 */
export const FilterOperatorTypes = {
  Equals: 1,
  NotEqualTo: 2,
  StartsWith: 3,
  Contains: 4,
  DoesNotContain: 5,
  LessThan: 6,
  GreaterThan: 7,
  LessOrEqual: 8,
  GreaterOrEqual: 9,
  Includes: 10,
  Excludes: 11,
  Within: 12,
} as const;

/**
 * Record view layout options — Definitions.cs lines 46-50.
 * Determines the column structure of record detail/manage views.
 */
export const RecordViewLayouts = {
  OneColumn: 1,
  TwoColumns: 2,
} as const;

/**
 * Record view column identifiers — Definitions.cs lines 52-56.
 * Used to place fields into left or right columns in two-column layouts.
 */
export const RecordViewColumns = {
  Left: 1,
  Right: 2,
} as const;

/**
 * Currency symbol placement relative to value — Definitions.cs lines 58-62.
 * Before: $100, After: 100€
 */
export const CurrencySymbolPlacement = {
  Before: 1,
  After: 2,
} as const;

/**
 * Return types for formula/computed fields — Definitions.cs lines 92-101.
 * Determines how the result of a formula is formatted and stored.
 */
export const FormulaFieldReturnType = {
  Checkbox: 1,
  Currency: 2,
  Date: 3,
  DateTime: 4,
  Number: 5,
  Percent: 6,
  Text: 7,
} as const;

/**
 * Page type identifiers — PageType.cs lines 6-22.
 * Determines the purpose and rendering behavior of each page.
 */
export const PageType = {
  Home: 0,
  Site: 1,
  Application: 2,
  RecordList: 3,
  RecordCreate: 4,
  RecordDetails: 5,
  RecordManage: 6,
} as const;

/**
 * Component display mode — ComponentMode.cs lines 3-9.
 * Controls which rendering mode a page-builder component is in.
 */
export const ComponentMode = {
  Display: 1,
  Design: 2,
  Options: 3,
  Help: 4,
} as const;

// =============================================================================
// Phase 5: CurrencyType Interface and Common Currencies
// =============================================================================

/**
 * Currency type definition matching the C# CurrencyType class
 * from Definitions.cs lines 64-90.
 */
export interface CurrencyType {
  /** Display symbol (e.g. "$", "€") */
  symbol: string;
  /** Native symbol variant */
  symbolNative: string;
  /** Full currency name (e.g. "US Dollar") */
  name: string;
  /** Plural form of currency name */
  namePlural: string;
  /** ISO 4217 currency code (e.g. "USD") */
  code: string;
  /** Number of decimal digits for formatting */
  decimalDigits: number;
  /** Rounding increment (0 = no special rounding) */
  rounding: number;
  /** Symbol placement: 1 = Before value, 2 = After value */
  symbolPlacement: number;
}

/**
 * Common currencies subset from the full Helpers.cs catalog (~170 entries).
 * The complete catalog can be loaded lazily from the API when needed.
 */
export const COMMON_CURRENCIES: Record<string, CurrencyType> = {
  USD: {
    symbol: '$',
    symbolNative: '$',
    name: 'US Dollar',
    namePlural: 'US dollars',
    code: 'USD',
    decimalDigits: 2,
    rounding: 0,
    symbolPlacement: CurrencySymbolPlacement.Before,
  },
  EUR: {
    symbol: '€',
    symbolNative: '€',
    name: 'Euro',
    namePlural: 'euros',
    code: 'EUR',
    decimalDigits: 2,
    rounding: 0,
    symbolPlacement: CurrencySymbolPlacement.Before,
  },
  GBP: {
    symbol: '£',
    symbolNative: '£',
    name: 'British Pound',
    namePlural: 'British pounds',
    code: 'GBP',
    decimalDigits: 2,
    rounding: 0,
    symbolPlacement: CurrencySymbolPlacement.Before,
  },
};

// =============================================================================
// Phase 6: API Route Constants
// =============================================================================

/**
 * API version identifier.
 * Migrated from monolith's api/v3/en_US/* to target v1/* scheme.
 */
export const API_VERSION = 'v1';

/** Base path prefix for all API endpoints */
export const API_BASE_PATH = `/api/${API_VERSION}`;

/**
 * Per-service API route constants mapped from WebApiController.cs patterns.
 * Each route is prefixed with the API_BASE_PATH and organized by bounded context.
 */
export const API_ROUTES = {
  // Identity service
  AUTH_LOGIN: `${API_BASE_PATH}/auth/login`,
  AUTH_LOGOUT: `${API_BASE_PATH}/auth/logout`,
  AUTH_REFRESH: `${API_BASE_PATH}/auth/refresh`,
  USERS: `${API_BASE_PATH}/users`,
  ROLES: `${API_BASE_PATH}/roles`,

  // Entity Management service
  ENTITIES: `${API_BASE_PATH}/entities`,
  RECORDS: `${API_BASE_PATH}/records`,
  RELATIONS: `${API_BASE_PATH}/relations`,
  DATASOURCES: `${API_BASE_PATH}/datasources`,

  // CRM service
  ACCOUNTS: `${API_BASE_PATH}/crm/accounts`,
  CONTACTS: `${API_BASE_PATH}/crm/contacts`,

  // Project / Inventory service
  TASKS: `${API_BASE_PATH}/projects/tasks`,
  TIMELOGS: `${API_BASE_PATH}/projects/timelogs`,
  PRODUCTS: `${API_BASE_PATH}/inventory/products`,

  // Invoicing service
  INVOICES: `${API_BASE_PATH}/invoicing/invoices`,
  PAYMENTS: `${API_BASE_PATH}/invoicing/payments`,

  // Notifications service
  NOTIFICATIONS: `${API_BASE_PATH}/notifications`,
  EMAILS: `${API_BASE_PATH}/notifications/emails`,

  // File Management service
  FILES: `${API_BASE_PATH}/files`,
  FILE_UPLOAD: `${API_BASE_PATH}/files/upload`,
  FILE_DOWNLOAD: `${API_BASE_PATH}/files/download`,

  // Workflow service
  WORKFLOWS: `${API_BASE_PATH}/workflows`,

  // Plugin service
  PLUGINS: `${API_BASE_PATH}/plugins`,

  // Reporting service
  REPORTS: `${API_BASE_PATH}/reports`,

  // Search
  SEARCH: `${API_BASE_PATH}/search`,
} as const;

// =============================================================================
// Phase 7: Application Defaults and Environment
// =============================================================================

/**
 * Application default settings replacing ErpSettings static properties.
 * These serve as fallback values when user/system configuration is absent.
 */
export const APP_DEFAULTS = {
  /** Default timezone — replaced at runtime by user/system config */
  TIMEZONE: 'UTC',
  /** Default locale — replaces ErpSettings.Locale */
  LOCALE: 'en-US',
  /** Default currency ISO code */
  CURRENCY_CODE: 'USD',
  /** Default pagination page size */
  PAGE_SIZE: 10,
  /** Maximum allowed pagination page size */
  MAX_PAGE_SIZE: 100,
  /** PostgreSQL identifier length limit from EntityManager.cs */
  ENTITY_NAME_MAX_LENGTH: 63,
  /** Default text snippet length from RenderService.cs */
  SNIPPET_MAX_LENGTH: 150,
} as const;

/**
 * Environment-driven settings populated from Vite environment variables.
 * VITE_API_URL: API Gateway base URL (defaults to LocalStack endpoint)
 * VITE_IS_LOCAL: Flag indicating LocalStack development mode
 */
export const ENV = {
  API_URL: import.meta.env.VITE_API_URL || 'http://localhost:4566',
  IS_LOCAL: import.meta.env.VITE_IS_LOCAL === 'true',
} as const;
