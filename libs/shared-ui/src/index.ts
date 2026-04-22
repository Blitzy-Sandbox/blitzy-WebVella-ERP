/**
 * @webvella-erp/shared-ui — Public API Barrel Export
 *
 * This file is the single entry-point for the shared-ui library.
 * Consumers import via the path alias:
 *
 *   import { DataTable, useAuth, Entity } from '@webvella-erp/shared-ui';
 *
 * Organisation:
 *   1. Components  — DataTable, DynamicForm, FieldRenderer and helpers
 *   2. Hooks       — useAuth, useApi, usePagination
 *   3. Types       — All shared TypeScript interfaces, enums, and type aliases
 *
 * Rules enforced:
 *   • Pure re-exports only — no logic, no side-effects
 *   • Named exports only — no default exports
 *   • `export type` for interfaces and type aliases (compile-time only)
 *   • Regular `export` for enums, classes, functions, and constants (runtime values)
 */

// ---------------------------------------------------------------------------
// 1. Components
// ---------------------------------------------------------------------------

// DataTable — sortable / filterable / paginated data grid component
export { DataTable } from './components/DataTable';
export type { DataTableProps, DataTableColumn } from './components/DataTable';

// DynamicForm — dynamic form builder with validation context
export { DynamicForm, FormContext, useFormContext } from './components/Form';
export type {
  DynamicFormProps,
  FormContextValue,
  ValidationError,
} from './components/Form';

// FieldRenderer — dynamic field-type dispatch component and helpers
export { FieldRenderer, FIELD_TYPE_LABELS } from './components/FieldComponents';
export type { FieldRendererProps } from './components/FieldComponents';

// ---------------------------------------------------------------------------
// 2. Hooks
// ---------------------------------------------------------------------------

// useAuth — Cognito JWT authentication hook
export { useAuth } from './hooks/useAuth';
export type { AuthState, AuthActions, LoginCredentials } from './hooks/useAuth';

// useApi — HTTP API client hook with retry, auth-header injection, and error handling
export { useApi, ApiError } from './hooks/useApi';
export type {
  UseApiOptions,
  RequestConfig,
  ApiClient,
} from './hooks/useApi';

// usePagination — URL-synced pagination / sort state management hook
export { usePagination } from './hooks/usePagination';
export type {
  PaginationState,
  PaginationActions,
  PaginationConfig,
} from './hooks/usePagination';

// ---------------------------------------------------------------------------
// 3. Types — shared TypeScript interfaces, enums, and type aliases
// ---------------------------------------------------------------------------

// Entity & field metadata
export type {
  Entity,
  InputEntity,
  RecordPermissions,
  Field,
  FieldPermissions,
  SelectOption,
  CurrencyType,
} from './types';

// Enums — runtime values, must NOT use `export type`
export {
  FieldType,
  EntityRelationType,
  FilterType,
  ComponentMode,
  WvFieldAccess,
  WvLabelRenderMode,
  WvFieldRenderMode,
  QuerySortType,
  ScreenMessageType,
} from './types';

// Entity relations
export type { EntityRelation } from './types';

// Records
export type { EntityRecord, EntityRecordList } from './types';

// Users & roles
export type {
  ErpUser,
  ErpUserPreferences,
  UserComponentUsage,
  ErpRole,
} from './types';

// Filtering
export type { Filter } from './types';

// API response models
export type {
  ErrorModel,
  AccessWarningModel,
  BaseResponseModel,
  ApiResponse,
} from './types';

// Query / sort helpers
export type { QuerySortObject } from './types';

// Screen messages (toasts / alerts)
export type { ScreenMessage } from './types';
