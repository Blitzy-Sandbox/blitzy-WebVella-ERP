/**
 * Entity, Field (20+ types), and Relation TypeScript interfaces.
 *
 * This is the largest type file in the application — it defines:
 * - 3 enums: FieldType (21 values), EntityRelationType, GeographyFieldFormat
 * - Base Field interface plus 20 concrete field-type interfaces
 * - AnyField discriminated union type
 * - Entity, InputEntity, EntityRelation, and related DTOs
 * - 6 API response wrapper types
 *
 * Converted from C# source files:
 *   - WebVella.Erp/Api/Models/Entity.cs
 *   - WebVella.Erp/Api/Models/EntityRelation.cs
 *   - WebVella.Erp/Api/Models/FieldTypes/FieldType.cs
 *   - WebVella.Erp/Api/Models/FieldTypes/BaseField.cs
 *   - WebVella.Erp/Api/Models/FieldTypes/{AutoNumber..Url}Field.cs (20 files)
 *   - WebVella.Erp/Api/Models/FieldTypes/RelationFieldMeta.cs
 *   - WebVella.Erp/Api/Definitions.cs (CurrencyType)
 */

import type { BaseResponseModel } from './common';

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/**
 * Discriminator for the 21 supported entity field types.
 *
 * Mirrors C# `FieldType` enum from FieldTypes/FieldType.cs.
 * Numeric values match the C# backing integers exactly so that JSON
 * round-trips between the .NET backend and this TypeScript frontend are
 * transparent.
 */
export const enum FieldType {
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
 * Cardinality of an entity relation.
 *
 * Mirrors C# `EntityRelationType` enum from EntityRelation.cs.
 */
export const enum EntityRelationType {
  OneToOne = 1,
  OneToMany = 2,
  ManyToMany = 3,
}

/**
 * Output format for geography field values.
 *
 * Mirrors C# `GeographyFieldFormat` enum from GeographyField.cs.
 *   - GeoJSON → ST_AsGeoJSON (default)
 *   - Text    → ST_AsText
 */
export const enum GeographyFieldFormat {
  GeoJSON = 1,
  Text = 2,
}

// ---------------------------------------------------------------------------
// Shared Field Models
// ---------------------------------------------------------------------------

/**
 * Role-based read/update permissions attached to a single field.
 *
 * Mirrors C# `FieldPermissions` from BaseField.cs (lines 441-455).
 * Each array contains role GUIDs (as strings) that are granted the
 * corresponding permission.
 */
export interface FieldPermissions {
  /** Role IDs (GUIDs) with read access. */
  canRead: string[];
  /** Role IDs (GUIDs) with update access. */
  canUpdate: string[];
}

/**
 * A single option within a Select or MultiSelect field.
 *
 * Mirrors C# `SelectOption` from SelectField.cs (lines 33-70).
 * JSON wire property name for `iconClass` is "icon_class".
 */
export interface SelectOption {
  /** Option value stored in the database. */
  value: string;
  /** Human-readable display label. */
  label: string;
  /** CSS icon class for the option (JSON: icon_class). */
  iconClass: string;
  /** Accent colour for the option. */
  color: string;
}

/**
 * ISO currency definition used by CurrencyField.
 *
 * Mirrors C# `CurrencyType` from Definitions.cs.
 * Maps ISO currency metadata for display and formatting.
 */
export interface CurrencyType {
  /** Primary currency symbol (e.g. "$"). */
  symbol: string;
  /** Native currency symbol (e.g. "$"). */
  symbolNative: string;
  /** Full English name (e.g. "US Dollar"). */
  name: string;
  /** Plural English name (e.g. "US dollars"). */
  namePlural: string;
  /** ISO 4217 alphabetic code (e.g. "USD"). */
  code: string;
  /** Number of decimal digits for display (e.g. 2). */
  decimalDigits: number;
  /** Rounding increment (e.g. 0 for no special rounding). */
  rounding: number;
}

// ---------------------------------------------------------------------------
// Base Field Interface
// ---------------------------------------------------------------------------

/**
 * Abstract base interface shared by all 20 concrete field types.
 *
 * Mirrors C# abstract `Field` class from BaseField.cs (lines 226-413).
 * The `fieldType` property acts as the discriminator for the AnyField
 * discriminated union.
 */
export interface Field {
  /** Unique field identifier (GUID as string). */
  id: string;
  /** Machine-readable field name (e.g. "first_name"). */
  name: string;
  /** Human-readable display label. */
  label: string;
  /** Placeholder text shown in empty input controls. */
  placeholderText: string;
  /** Long-form field description. */
  description: string;
  /** Contextual help text displayed near the field. */
  helpText: string;
  /** Whether a value is required for this field. */
  required: boolean;
  /** Whether this field enforces a uniqueness constraint. */
  unique: boolean;
  /** Whether this field is included in search indexes. */
  searchable: boolean;
  /** Whether changes to this field are audit-logged. */
  auditable: boolean;
  /** Whether this is a system-managed (non-user-editable) field. */
  system: boolean;
  /** Role-based read/update permissions for this field. */
  permissions: FieldPermissions;
  /** Whether field-level security is enabled. */
  enableSecurity: boolean;
  /** Name of the entity this field belongs to. */
  entityName: string;
  /** Discriminator identifying the concrete field type. */
  fieldType: FieldType;
}

// ---------------------------------------------------------------------------
// Concrete Field Type Interfaces (20 types)
// ---------------------------------------------------------------------------

/**
 * Auto-incrementing numeric field.
 *
 * Mirrors C# `AutoNumberField` from AutoNumberField.cs.
 */
export interface AutoNumberField extends Field {
  fieldType: FieldType.AutoNumberField;
  /** Starting/default value (C# decimal? → number | null). */
  defaultValue: number | null;
  /** Display format string (e.g. "{0:00000}"). */
  displayFormat: string;
  /** Number from which auto-increment begins. */
  startingNumber: number | null;
}

/**
 * Boolean checkbox field.
 *
 * Mirrors C# `CheckboxField` from CheckboxField.cs.
 */
export interface CheckboxField extends Field {
  fieldType: FieldType.CheckboxField;
  /** Default checked state. */
  defaultValue: boolean | null;
}

/**
 * Monetary value field with currency metadata.
 *
 * Mirrors C# `CurrencyField` from CurrencyField.cs.
 */
export interface CurrencyField extends Field {
  fieldType: FieldType.CurrencyField;
  /** Default monetary value. */
  defaultValue: number | null;
  /** Minimum allowed value. */
  minValue: number | null;
  /** Maximum allowed value. */
  maxValue: number | null;
  /** Currency definition (ISO metadata). */
  currency: CurrencyType;
}

/**
 * Date-only field (no time component).
 *
 * Mirrors C# `DateField` from DateField.cs.
 * C# DateTime? serialises to ISO 8601 string.
 */
export interface DateField extends Field {
  fieldType: FieldType.DateField;
  /** Default date value as ISO 8601 string. */
  defaultValue: string | null;
  /** Display format string. */
  format: string;
  /** Whether to use the current date as the default value. */
  useCurrentTimeAsDefaultValue: boolean | null;
}

/**
 * Date + time field.
 *
 * Mirrors C# `DateTimeField` from DateTimeField.cs.
 */
export interface DateTimeField extends Field {
  fieldType: FieldType.DateTimeField;
  /** Default date-time value as ISO 8601 string. */
  defaultValue: string | null;
  /** Display format string. */
  format: string;
  /** Whether to use the current date-time as the default value. */
  useCurrentTimeAsDefaultValue: boolean | null;
}

/**
 * Email address field with max-length constraint.
 *
 * Mirrors C# `EmailField` from EmailField.cs.
 */
export interface EmailField extends Field {
  fieldType: FieldType.EmailField;
  /** Default email address. */
  defaultValue: string;
  /** Maximum character length. */
  maxLength: number | null;
}

/**
 * File attachment reference field.
 *
 * Mirrors C# `FileField` from FileField.cs.
 */
export interface FileField extends Field {
  fieldType: FieldType.FileField;
  /** Default file path or URL. */
  defaultValue: string;
}

/**
 * Rich HTML content field.
 *
 * Mirrors C# `HtmlField` from HtmlField.cs.
 */
export interface HtmlField extends Field {
  fieldType: FieldType.HtmlField;
  /** Default HTML content. */
  defaultValue: string;
}

/**
 * Image file reference field.
 *
 * Mirrors C# `ImageField` from ImageField.cs.
 */
export interface ImageField extends Field {
  fieldType: FieldType.ImageField;
  /** Default image path or URL. */
  defaultValue: string;
}

/**
 * Multi-line (textarea) text field.
 *
 * Mirrors C# `MultiLineTextField` from MultiLineTextField.cs.
 */
export interface MultiLineTextField extends Field {
  fieldType: FieldType.MultiLineTextField;
  /** Default text content. */
  defaultValue: string;
  /** Maximum character length. */
  maxLength: number | null;
  /** Number of visible lines in the textarea control. */
  visibleLineNumber: number | null;
}

/**
 * Multi-value selection field (tags / checkboxes).
 *
 * Mirrors C# `MultiSelectField` from MultiSelectField.cs.
 * C# IEnumerable<string> → string[].
 */
export interface MultiSelectField extends Field {
  fieldType: FieldType.MultiSelectField;
  /** Default selected values. */
  defaultValue: string[];
  /** Available options for selection. */
  options: SelectOption[];
}

/**
 * Numeric field with decimal precision and range constraints.
 *
 * Mirrors C# `NumberField` from NumberField.cs.
 * C# decimal? → number | null; C# byte? → number | null.
 */
export interface NumberField extends Field {
  fieldType: FieldType.NumberField;
  /** Default numeric value. */
  defaultValue: number | null;
  /** Minimum allowed value. */
  minValue: number | null;
  /** Maximum allowed value. */
  maxValue: number | null;
  /** Number of decimal places for display/storage. */
  decimalPlaces: number | null;
}

/**
 * Password field (no defaultValue — passwords are write-only).
 *
 * Mirrors C# `PasswordField` from PasswordField.cs.
 */
export interface PasswordField extends Field {
  fieldType: FieldType.PasswordField;
  /** Maximum character length. */
  maxLength: number | null;
  /** Minimum character length. */
  minLength: number | null;
  /** Whether the stored value is encrypted. */
  encrypted: boolean | null;
}

/**
 * Percentage field with decimal precision and range constraints.
 *
 * Mirrors C# `PercentField` from PercentField.cs.
 */
export interface PercentField extends Field {
  fieldType: FieldType.PercentField;
  /** Default percentage value. */
  defaultValue: number | null;
  /** Minimum allowed value. */
  minValue: number | null;
  /** Maximum allowed value. */
  maxValue: number | null;
  /** Number of decimal places for display/storage. */
  decimalPlaces: number | null;
}

/**
 * Phone number field with format and max-length constraints.
 *
 * Mirrors C# `PhoneField` from PhoneField.cs.
 */
export interface PhoneField extends Field {
  fieldType: FieldType.PhoneField;
  /** Default phone number. */
  defaultValue: string;
  /** Phone number display format. */
  format: string;
  /** Maximum character length. */
  maxLength: number | null;
}

/**
 * GUID / UUID field.
 *
 * Mirrors C# `GuidField` from GuidField.cs.
 * C# Guid? → string | null.
 */
export interface GuidField extends Field {
  fieldType: FieldType.GuidField;
  /** Default GUID value as string. */
  defaultValue: string | null;
  /** Whether to auto-generate a new GUID as the default value. */
  generateNewId: boolean | null;
}

/**
 * Single-value dropdown / select field.
 *
 * Mirrors C# `SelectField` from SelectField.cs.
 */
export interface SelectField extends Field {
  fieldType: FieldType.SelectField;
  /** Default selected value. */
  defaultValue: string;
  /** Available options for selection. */
  options: SelectOption[];
}

/**
 * Single-line text field with max-length constraint.
 *
 * Mirrors C# `TextField` from TextField.cs.
 */
export interface TextField extends Field {
  fieldType: FieldType.TextField;
  /** Default text value. */
  defaultValue: string;
  /** Maximum character length. */
  maxLength: number | null;
}

/**
 * URL / hyperlink field.
 *
 * Mirrors C# `UrlField` from UrlField.cs.
 */
export interface UrlField extends Field {
  fieldType: FieldType.UrlField;
  /** Default URL value. */
  defaultValue: string;
  /** Maximum character length. */
  maxLength: number | null;
  /** Whether links open in a new browser tab/window. */
  openTargetInNewWindow: boolean | null;
}

/**
 * Geographic / spatial data field.
 *
 * Mirrors C# `GeographyField` from GeographyField.cs.
 * SRID defaults to 4326 (WGS 84) in the C# source.
 */
export interface GeographyField extends Field {
  fieldType: FieldType.GeographyField;
  /** Default geography value (GeoJSON or WKT string). */
  defaultValue: string;
  /** Maximum character length. */
  maxLength: number | null;
  /** Number of visible lines in the textarea control. */
  visibleLineNumber: number | null;
  /** Output format for the geography value. */
  format: GeographyFieldFormat | null;
  /** Spatial Reference System Identifier (default: 4326). */
  srid: number;
}

// ---------------------------------------------------------------------------
// Discriminated Union
// ---------------------------------------------------------------------------

/**
 * Union of all 20 concrete field type interfaces.
 *
 * Use the `fieldType` discriminator to narrow to a specific type:
 * ```ts
 * if (field.fieldType === FieldType.TextField) {
 *   // field is narrowed to TextField
 *   console.log(field.maxLength);
 * }
 * ```
 */
export type AnyField =
  | AutoNumberField
  | CheckboxField
  | CurrencyField
  | DateField
  | DateTimeField
  | EmailField
  | FileField
  | HtmlField
  | ImageField
  | MultiLineTextField
  | MultiSelectField
  | NumberField
  | PasswordField
  | PercentField
  | PhoneField
  | GuidField
  | SelectField
  | TextField
  | UrlField
  | GeographyField;

// ---------------------------------------------------------------------------
// Entity Models
// ---------------------------------------------------------------------------

/**
 * Role-based CRUD permissions for an entity's records.
 *
 * Mirrors C# `RecordPermissions` from Entity.cs (lines 80-93).
 * Each array contains role GUIDs (as strings) that are granted the
 * corresponding permission.
 */
export interface RecordPermissions {
  /** Role IDs with read access. */
  canRead: string[];
  /** Role IDs with create access. */
  canCreate: string[];
  /** Role IDs with update access. */
  canUpdate: string[];
  /** Role IDs with delete access. */
  canDelete: string[];
}

/**
 * Input DTO for creating or updating an entity.
 *
 * Mirrors C# `InputEntity` from Entity.cs (lines 7-35).
 * Optional properties use `?` to indicate they may be omitted in
 * create/update payloads.
 */
export interface InputEntity {
  /** Entity ID (GUID); optional for create, required for update. */
  id?: string | null;
  /** Machine-readable entity name (e.g. "contact"). */
  name: string;
  /** Human-readable display label. */
  label: string;
  /** Plural form of the display label. */
  labelPlural: string;
  /** Whether this is a system-managed entity. */
  system?: boolean;
  /** Icon name for the entity (e.g. "fas fa-database"). */
  iconName: string;
  /** Accent colour for the entity. */
  color: string;
  /** Role-based record-level permissions. */
  recordPermissions: RecordPermissions;
  /** Field ID used as the screen/display identifier; null uses the record ID. */
  recordScreenIdField?: string | null;
}

/**
 * Full entity descriptor returned by the API.
 *
 * Mirrors C# `Entity` from Entity.cs (lines 37-77).
 * Includes the complete list of fields and a hash for cache validation.
 */
export interface Entity {
  /** Unique entity identifier (GUID as string). */
  id: string;
  /** Machine-readable entity name (e.g. "contact"). */
  name: string;
  /** Human-readable display label. */
  label: string;
  /** Plural form of the display label. */
  labelPlural: string;
  /** Whether this is a system-managed entity. */
  system: boolean;
  /** Icon name for the entity. */
  iconName: string;
  /** Accent colour for the entity. */
  color: string;
  /** Role-based record-level permissions. */
  recordPermissions: RecordPermissions;
  /** All fields defined on this entity. */
  fields: Field[];
  /** Field ID used as the screen/display identifier; null uses the record ID. */
  recordScreenIdField?: string | null;
  /** ETag-like hash for cache validation. */
  hash: string;
}

// ---------------------------------------------------------------------------
// Entity Relation Models
// ---------------------------------------------------------------------------

/**
 * Full entity relation descriptor.
 *
 * Mirrors C# `EntityRelation` from EntityRelation.cs (lines 36-83).
 * Defines the cardinality and field linkage between two entities.
 */
export interface EntityRelation {
  /** Unique relation identifier (GUID as string). */
  id: string;
  /** Machine-readable relation name. */
  name: string;
  /** Human-readable display label. */
  label: string;
  /** Long-form relation description. */
  description: string;
  /** Whether this is a system-managed relation. */
  system: boolean;
  /** Cardinality of the relation (1:1, 1:N, N:N). */
  relationType: EntityRelationType;
  /** GUID of the origin (source) entity. */
  originEntityId: string;
  /** GUID of the origin field. */
  originFieldId: string;
  /** GUID of the target (destination) entity. */
  targetEntityId: string;
  /** GUID of the target field. */
  targetFieldId: string;
  /** Name of the origin entity. */
  originEntityName: string;
  /** Name of the origin field. */
  originFieldName: string;
  /** Name of the target entity. */
  targetEntityName: string;
  /** Name of the target field. */
  targetFieldName: string;
}

/**
 * A single relation option item used in relation pickers.
 *
 * Mirrors C# `EntityRelationOptionsItem` from EntityRelation.cs (lines 87-100).
 * The `type` property is always the static string "relationOptions".
 */
export interface EntityRelationOptionsItem {
  /** Static discriminator — always "relationOptions". */
  type: string;
  /** Relation ID (GUID); null when not yet saved. */
  relationId?: string | null;
  /** Machine-readable relation name. */
  relationName: string;
  /** Direction of the relation from the current entity's perspective. */
  direction: string;
}

/**
 * Input DTO for updating many-to-many relation records.
 *
 * Mirrors C# `InputEntityRelationRecordUpdateModel` from EntityRelation.cs.
 * Used to attach/detach target records from an origin record via a named
 * relation.
 */
export interface InputEntityRelationRecordUpdateModel {
  /** Machine-readable name of the relation. */
  relationName: string;
  /** GUID of the origin-side record. */
  originFieldRecordId: string;
  /** GUIDs of target records to attach. */
  attachTargetFieldRecordIds: string[];
  /** GUIDs of target records to detach. */
  detachTargetFieldRecordIds: string[];
}

// ---------------------------------------------------------------------------
// API Response Wrappers
// ---------------------------------------------------------------------------

/**
 * API response containing a single Entity.
 *
 * Mirrors C# `EntityResponse` from Entity.cs (lines 95-99).
 */
export interface EntityResponse extends BaseResponseModel {
  object: Entity;
}

/**
 * API response containing a list of Entities.
 *
 * Mirrors C# `EntityListResponse` from Entity.cs (lines 101-105).
 */
export interface EntityListResponse extends BaseResponseModel {
  object: Entity[];
}

/**
 * API response containing a single EntityRelation.
 *
 * Mirrors C# `EntityRelationResponse` from EntityRelation.cs (lines 103-107).
 */
export interface EntityRelationResponse extends BaseResponseModel {
  object: EntityRelation;
}

/**
 * API response containing a list of EntityRelations.
 *
 * Mirrors C# `EntityRelationListResponse` from EntityRelation.cs (lines 110-114).
 */
export interface EntityRelationListResponse extends BaseResponseModel {
  object: EntityRelation[];
}

/**
 * API response containing a single Field.
 *
 * Mirrors C# `FieldResponse` from BaseField.cs (lines 428-432).
 */
export interface FieldResponse extends BaseResponseModel {
  object: Field;
}

/**
 * API response containing a list of Fields wrapped in a FieldList object.
 *
 * Mirrors C# `FieldListResponse` from BaseField.cs (lines 434-438).
 * The C# `FieldList` wrapper is preserved: `{ fields: Field[] }`.
 */
export interface FieldListResponse extends BaseResponseModel {
  object: { fields: Field[] };
}
