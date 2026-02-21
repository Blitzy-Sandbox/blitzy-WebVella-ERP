/**
 * Canonical Shared TypeScript Type Definitions for WebVella ERP Frontend
 *
 * This file is the single source of truth for all shared interfaces, enums,
 * and type aliases derived from the C# monolith's DTO models. Every frontend
 * consumer (components, hooks, pages, stores, API clients) imports types
 * from this file.
 *
 * RULES:
 * - No imports — all types are self-contained
 * - All exports are named — no default exports
 * - Guid → string, DateTime → string (ISO 8601), dynamic → unknown
 * - List<T> → T[], nullable → | null or optional (?)
 * - [JsonIgnore] properties are excluded
 * - Enum numeric values match C# source exactly
 */

// ============================================================================
// SECTION 1: Entity Types
// Source: WebVella.Erp/Api/Models/Entity.cs
// ============================================================================

/**
 * Record-level CRUD permissions — lists of role GUIDs.
 * Source: WebVella.Erp/Api/Models/Entity.cs (lines 80-93), class RecordPermissions
 */
export interface RecordPermissions {
  /** List<Guid> of role IDs that can read records */
  canRead: string[];
  /** List<Guid> of role IDs that can create records */
  canCreate: string[];
  /** List<Guid> of role IDs that can update records */
  canUpdate: string[];
  /** List<Guid> of role IDs that can delete records */
  canDelete: string[];
}

/**
 * Full entity descriptor — materialized/persisted entity metadata.
 * Source: WebVella.Erp/Api/Models/Entity.cs (lines 38-77), class Entity
 * JSON property names from [JsonProperty] attributes are used as field names.
 */
export interface Entity {
  /** Guid → string (UUID format) */
  id: string;
  /** Entity system name, default "" */
  name: string;
  /** Entity display label, default "" */
  label: string;
  /** JSON: "labelPlural" — plural form of label */
  labelPlural: string;
  /** Whether this is a system entity, default false */
  system: boolean;
  /** JSON: "iconName" — icon CSS class, default "" */
  iconName: string;
  /** Entity color, default "" */
  color: string;
  /** JSON: "recordPermissions" — role-based CRUD permissions */
  recordPermissions: RecordPermissions;
  /** JSON: "fields" — List<Field> of entity field definitions */
  fields: Field[];
  /** JSON: "record_screen_id_field" — Guid? → string | null, nullable screen ID field override */
  recordScreenIdField: string | null;
  /** Internal set, readonly on client — entity metadata hash */
  hash: string;
}

/**
 * Input entity for create/update operations — nullable fields for partial updates.
 * Source: WebVella.Erp/Api/Models/Entity.cs (lines 7-35), class InputEntity
 */
export interface InputEntity {
  /** Guid? → optional string */
  id?: string;
  name?: string;
  label?: string;
  labelPlural?: string;
  system?: boolean;
  iconName?: string;
  color?: string;
  recordPermissions?: RecordPermissions;
  /** JSON: "record_screen_id_field" — Guid? → string | null */
  recordScreenIdField?: string | null;
}

// ============================================================================
// SECTION 2: Field Types
// Source: WebVella.Erp/Api/Models/FieldTypes/FieldType.cs
//         WebVella.Erp/Api/Models/FieldTypes/BaseField.cs
//         WebVella.Erp/Api/Models/FieldTypes/SelectField.cs
// ============================================================================

/**
 * Discriminator enum for entity field types.
 * Source: WebVella.Erp/Api/Models/FieldTypes/FieldType.cs (lines 5-49)
 * Maps 1:1 to the C# FieldType enum values.
 */
export enum FieldType {
  AutoNumberField = 1,
  CheckboxField = 2,
  CurrencyField = 3,
  DateField = 4,
  DateTimeField = 5,
  EmailField = 6,
  FileField = 7,
  HtmlField = 8,
  ImageField = 9,
  MultiLineTextField = 10,
  MultiSelectField = 11,
  NumberField = 12,
  PasswordField = 13,
  PercentField = 14,
  PhoneField = 15,
  GuidField = 16,
  SelectField = 17,
  TextField = 18,
  UrlField = 19,
  RelationField = 20,
  GeographyField = 21,
}

/**
 * Field-level read/update permissions — lists of role GUIDs.
 * Source: WebVella.Erp/Api/Models/FieldTypes/BaseField.cs (lines 442-455), class FieldPermissions
 */
export interface FieldPermissions {
  /** List<Guid> of role IDs that can read this field */
  canRead: string[];
  /** List<Guid> of role IDs that can update this field */
  canUpdate: string[];
}

/**
 * Dropdown/select option definition.
 * Source: WebVella.Erp/Api/Models/FieldTypes/SelectField.cs (lines 34-70), class SelectOption
 */
export interface SelectOption {
  /** Option value, default "" */
  value: string;
  /** Option display label, default "" */
  label: string;
  /** JSON: "icon_class" — icon CSS class, default "" */
  iconClass: string;
  /** Option color, default "" */
  color: string;
}

/**
 * Currency type for CurrencyField.
 * Source: WebVella.Erp/Api/Definitions.cs, CurrencyType class
 */
export interface CurrencyType {
  /** Currency symbol (e.g., "$") */
  symbol: string;
  /** Native currency symbol (e.g., "$") */
  symbolNative: string;
  /** Currency name (e.g., "US Dollar") */
  name: string;
  /** Plural currency name (e.g., "US dollars") */
  namePlural: string;
  /** ISO 4217 currency code (e.g., "USD") */
  code: string;
  /** Number of decimal digits */
  decimalDigits: number;
  /** Rounding increment */
  rounding: number;
  /** Symbol placement (0 = before, 1 = after) */
  symbolPlacement: number;
}

/**
 * Materialized field definition — full field metadata.
 * Source: WebVella.Erp/Api/Models/FieldTypes/BaseField.cs (lines 227-413), abstract class Field
 * JSON property names from [JsonProperty] attributes used as field names.
 * Type-specific properties are optional since they vary by fieldType.
 */
export interface Field {
  /** Guid → string — unique field identifier */
  id: string;
  /** Field system name */
  name: string;
  /** Field display label */
  label: string;
  /** JSON: "placeholderText" — input placeholder text */
  placeholderText: string;
  /** Field description */
  description: string;
  /** JSON: "helpText" — help tooltip text */
  helpText: string;
  /** Whether this field is required, default false */
  required: boolean;
  /** Whether this field value must be unique, default false */
  unique: boolean;
  /** Whether this field is searchable, default false */
  searchable: boolean;
  /** Whether changes to this field are audited, default false */
  auditable: boolean;
  /** Whether this is a system field, default false */
  system: boolean;
  /** JSON: "permissions" — field-level read/update permissions */
  permissions: FieldPermissions;
  /** JSON: "enableSecurity" — whether permission checking is active, default false */
  enableSecurity: boolean;
  /** JSON: "entityName" — owning entity name */
  entityName: string;
  /** Discriminator — derived from GetFieldType() */
  fieldType: FieldType;

  // Type-specific properties (union-able by fieldType)

  /** Default field value — varies by field type */
  defaultValue?: unknown;
  /** Select/MultiSelect options list */
  options?: SelectOption[];
  /** Maximum character length for text-based fields */
  maxLength?: number;
  /** Minimum allowed value for numeric fields */
  minValue?: number;
  /** Maximum allowed value for numeric fields */
  maxValue?: number;
  /** Decimal places for Number/Percent/Currency fields */
  decimalPlaces?: number;
  /** Format string for Date/DateTime/Phone/Geography fields */
  format?: string;
  /** Whether to use current time as default for Date/DateTime fields */
  useCurrentTimeAsDefaultValue?: boolean;
  /** Whether to generate a new GUID as default for Guid fields */
  generateNewId?: boolean;
  /** Whether URL links open in a new window for Url fields */
  openTargetInNewWindow?: boolean;
  /** Whether the value is encrypted for Password fields */
  encrypted?: boolean;
  /** Number of visible lines for MultiLineText/Geography fields */
  visibleLineNumber?: number;
  /** Starting number for AutoNumber fields */
  startingNumber?: number;
  /** Display format string for AutoNumber fields */
  displayFormat?: string;
  /** Currency type reference for Currency fields */
  currency?: CurrencyType;
  /** Spatial Reference ID for Geography fields */
  srid?: number;
}

// ============================================================================
// SECTION 3: Relation Types
// Source: WebVella.Erp/Api/Models/EntityRelation.cs
// ============================================================================

/**
 * Entity relation type discriminator.
 * Source: WebVella.Erp/Api/Models/EntityRelation.cs (lines 9-33), enum EntityRelationType
 */
export enum EntityRelationType {
  OneToOne = 1,
  OneToMany = 2,
  ManyToMany = 3,
}

/**
 * Entity relation definition — describes a relationship between two entities.
 * Source: WebVella.Erp/Api/Models/EntityRelation.cs (lines 36-84), class EntityRelation
 */
export interface EntityRelation {
  /** Guid → string — unique relation identifier */
  id: string;
  /** Relation system name */
  name: string;
  /** Relation display label */
  label: string;
  /** Relation description */
  description: string;
  /** Whether this is a system relation */
  system: boolean;
  /** JSON: "relationType" — cardinality discriminator */
  relationType: EntityRelationType;
  /** JSON: "originEntityId" — Guid of origin entity */
  originEntityId: string;
  /** JSON: "originFieldId" — Guid of origin field */
  originFieldId: string;
  /** JSON: "targetEntityId" — Guid of target entity */
  targetEntityId: string;
  /** JSON: "targetFieldId" — Guid of target field */
  targetFieldId: string;
  /** Denormalized origin entity name */
  originEntityName: string;
  /** Denormalized origin field name */
  originFieldName: string;
  /** Denormalized target entity name */
  targetEntityName: string;
  /** Denormalized target field name */
  targetFieldName: string;
}

// ============================================================================
// SECTION 4: Record Types
// Source: WebVella.Erp/Api/Models/EntityRecord.cs
//         WebVella.Erp/Api/Models/EntityRecordList.cs
// ============================================================================

/**
 * Dynamic entity record — key-value map (extends Expando in C#).
 * Source: WebVella.Erp/Api/Models/EntityRecord.cs, class EntityRecord : Expando
 * Records are dynamically typed — keys are field names, values are field values.
 */
export type EntityRecord = Record<string, unknown>;

/**
 * List of entity records with total count for pagination.
 * Source: WebVella.Erp/Api/Models/EntityRecordList.cs, class EntityRecordList : List<EntityRecord>
 */
export interface EntityRecordList {
  /** JSON: "total_count" — total number of records matching the query, default 0 */
  totalCount: number;
  /** The actual list of entity records */
  records: EntityRecord[];
}

// ============================================================================
// SECTION 5: User / Role Types
// Source: WebVella.Erp/Api/Models/ErpUser.cs
//         WebVella.Erp/Api/Models/ErpRole.cs
// ============================================================================

/**
 * Component usage tracking for user preferences.
 * Source: WebVella.Erp/Api/Models/ErpUser.cs (referenced via ErpUserPreferences)
 */
export interface UserComponentUsage {
  /** Name of the component */
  componentName: string;
  /** Number of times the component has been used */
  count: number;
}

/**
 * User preferences for UI customization.
 * Source: WebVella.Erp/Api/Models/ErpUser.cs (referenced via ErpUser.Preferences)
 */
export interface ErpUserPreferences {
  /** Sidebar size preference */
  sidebarSize?: string;
  /** List of component usage tracking entries */
  componentUsageList?: UserComponentUsage[];
  /** Generic component data storage */
  componentData?: Record<string, unknown>;
}

/**
 * ERP user profile (serialized properties only — excludes [JsonIgnore] fields).
 * Source: WebVella.Erp/Api/Models/ErpUser.cs (lines 9-68), class ErpUser
 * Note: password, enabled, verified, roles are [JsonIgnore] in source and excluded.
 */
export interface ErpUser {
  /** Guid → string, default empty */
  id: string;
  /** Username, default "" */
  username: string;
  /** Email address, default "" */
  email: string;
  /** JSON: "firstName" — first name, default "" */
  firstName: string;
  /** JSON: "lastName" — last name, default "" */
  lastName: string;
  /** Profile image URL */
  image: string;
  /** JSON: "createdOn" — DateTime → ISO 8601 string */
  createdOn: string;
  /** JSON: "lastLoggedIn" — DateTime? → string | null */
  lastLoggedIn: string | null;
  /** JSON: "is_admin" — computed from roles, true if user has administrator role */
  isAdmin: boolean;
  /** JSON: "preferences" — user preferences for UI customization */
  preferences?: ErpUserPreferences;
}

/**
 * ERP role definition.
 * Source: WebVella.Erp/Api/Models/ErpRole.cs (lines 7-17), class ErpRole
 */
export interface ErpRole {
  /** Guid → string — unique role identifier */
  id: string;
  /** Role name */
  name: string;
  /** Role description */
  description: string;
}

// ============================================================================
// SECTION 6: Filter Types
// Source: WebVella.Erp.Web/Models/FilterType.cs
//         WebVella.Erp.Web/Models/Filter.cs
// ============================================================================

/**
 * Filter operator discriminator enum.
 * Source: WebVella.Erp.Web/Models/FilterType.cs (lines 9-36)
 * Has 12 operators plus Undefined=0.
 */
export enum FilterType {
  Undefined = 0,
  STARTSWITH = 1,
  CONTAINS = 2,
  EQ = 3,
  NOT = 4,
  LT = 5,
  LTE = 6,
  GT = 7,
  GTE = 8,
  REGEX = 9,
  FTS = 10,
  BETWEEN = 11,
  NOTBETWEEN = 12,
}

/**
 * Filter definition for list/grid filtering.
 * Source: WebVella.Erp.Web/Models/Filter.cs (lines 8-24), class Filter
 */
export interface Filter {
  /** Filter field name, default "" */
  name: string;
  /** Filter operator type, default FilterType.Undefined */
  type: FilterType;
  /** Filter value — dynamic → unknown */
  value: unknown;
  /** Second filter value — used for Between/NotBetween, dynamic → unknown */
  value2: unknown;
  /** Filter prefix — some lists have prefix, default "" */
  prefix: string;
}

// ============================================================================
// SECTION 7: UI Types
// Source: WebVella.Erp.Web/Models/ComponentMode.cs
//         WebVella.TagHelpers package enums
// ============================================================================

/**
 * Page component rendering mode.
 * Source: WebVella.Erp.Web/Models/ComponentMode.cs (lines 3-9)
 */
export enum ComponentMode {
  Display = 1,
  Design = 2,
  Options = 3,
  Help = 4,
}

/**
 * Field access level — controls field editability.
 * Source: WebVella.TagHelpers package (WvFieldAccess enum)
 * Values discovered from monolith usage: Full, ReadOnly, Forbidden, FullAndCreate
 */
export enum WvFieldAccess {
  Full = 0,
  ReadOnly = 1,
  Forbidden = 2,
  FullAndCreate = 3,
}

/**
 * Label rendering mode for field components.
 * Source: WebVella.TagHelpers package (WvLabelRenderMode enum)
 * Values discovered from monolith usage: Undefined, Stacked, Horizontal, Hidden
 */
export enum WvLabelRenderMode {
  Undefined = 0,
  Stacked = 1,
  Horizontal = 2,
  Hidden = 3,
}

/**
 * Field rendering mode — controls how field components display.
 * Source: WebVella.TagHelpers package (WvFieldRenderMode enum)
 * Values discovered from monolith usage: Undefined, Display, Form, InlineEdit, Simple
 */
export enum WvFieldRenderMode {
  Undefined = 0,
  Display = 1,
  Form = 2,
  InlineEdit = 3,
  Simple = 4,
}

// ============================================================================
// SECTION 8: API Response Types
// Source: WebVella.Erp/Api/Models/BaseModels.cs
// ============================================================================

/**
 * Standard API error detail.
 * Source: WebVella.Erp/Api/Models/BaseModels.cs (lines 62-83), class ErrorModel
 */
export interface ErrorModel {
  /** Error key identifier */
  key: string;
  /** Error value */
  value: string;
  /** Human-readable error message */
  message: string;
}

/**
 * Access warning detail.
 * Source: WebVella.Erp/Api/Models/BaseModels.cs (lines 50-60), class AccessWarningModel
 */
export interface AccessWarningModel {
  /** Warning key identifier */
  key: string;
  /** Warning code */
  code: string;
  /** Human-readable warning message */
  message: string;
}

/**
 * Base API response envelope — common to all API responses.
 * Source: WebVella.Erp/Api/Models/BaseModels.cs (lines 8-38), class BaseResponseModel
 * Note: StatusCode is [JsonIgnore] in source, so excluded from TS type.
 */
export interface BaseResponseModel {
  /** DateTime → ISO 8601 string — response timestamp */
  timestamp: string;
  /** Whether the operation was successful */
  success: boolean;
  /** Human-readable response message */
  message: string;
  /** Response hash for cache validation, nullable */
  hash: string | null;
  /** List of error details */
  errors: ErrorModel[];
  /** List of access warning details */
  accessWarnings: AccessWarningModel[];
}

/**
 * Generic typed API response — extends BaseResponseModel with typed payload.
 * Source: WebVella.Erp/Api/Models/BaseModels.cs (lines 40-48), class ResponseModel
 * In C# the payload is `object Object`. In TS we make it generic.
 */
export interface ApiResponse<T = unknown> extends BaseResponseModel {
  /** JSON key "object" — the typed response payload */
  object: T;
}

// ============================================================================
// SECTION 9: Supporting Types
// Source: Various WebVella.Erp models
// ============================================================================

/**
 * Query sort direction.
 * Source: WebVella.Erp/Api/Models/QuerySortType.cs
 */
export enum QuerySortType {
  Ascending = 0,
  Descending = 1,
}

/**
 * Query sort object — defines sort field and direction.
 * Source: WebVella.Erp/Api/Models/QuerySortObject.cs
 */
export interface QuerySortObject {
  /** Name of the field to sort by */
  fieldName: string;
  /** Sort direction */
  sortType: QuerySortType;
}

/**
 * Screen message type for toast/alert notifications.
 * Source: WebVella.Erp.Web/Models/ScreenMessage.cs
 */
export enum ScreenMessageType {
  Success = 0,
  Error = 1,
  Warning = 2,
  Info = 3,
}

/**
 * Screen message for toast/alert notifications.
 * Source: WebVella.Erp.Web/Models/ScreenMessage.cs
 */
export interface ScreenMessage {
  /** Message severity type */
  type: ScreenMessageType;
  /** Message title */
  title: string;
  /** Message body */
  message: string;
}
