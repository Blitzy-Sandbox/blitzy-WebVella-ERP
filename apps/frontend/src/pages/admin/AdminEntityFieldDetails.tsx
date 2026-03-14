/**
 * AdminEntityFieldDetails — Read-Only Field Detail Page
 *
 * Replaces `WebVella.Erp.Plugins.SDK/Pages/entity/field-details.cshtml[.cs]`.
 * Displays all field properties in read-only mode, type-specific settings for
 * all 20 field types, an API security permission matrix, and a delete action
 * for non-system fields with a confirmation modal.
 *
 * Route: `/admin/entities/:entityId/fields/:fieldId`
 *
 * Source mapping:
 *  - field-details.cshtml.cs InitPage()  → useEntity(entityId) TanStack Query
 *  - field-details.cshtml.cs OnPost()    → useDeleteField() mutation + Modal
 *  - FieldCard display                   → FIELD_TYPE_CARDS static lookup
 *  - General section (read-only rows)    → ReadOnlyRow helper
 *  - Type-specific section (switch/case) → renderTypeSpecificSection()
 *  - API Security permission grid        → renderPermissionMatrix()
 *  - AdminPageUtils.GetUserRoles()       → useRoles() TanStack Query
 *  - AdminPageUtils.GetEntityAdminSubNav → ENTITY_SUB_NAV Link tabs
 *  - "Manage" anchor                     → Link to manage route
 *  - "Delete Field" / "Delete locked"    → button + Modal or disabled button
 */

import { useState, useCallback, useMemo } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useEntity, useEntityFields, useDeleteField } from '../../hooks/useEntities';
import { useRoles } from '../../hooks/useUsers';
import type {
  Entity,
  AnyField,
  FieldPermissions,
  SelectOption,
  CurrencyType,
} from '../../types/entity';
import { FieldType, GeographyFieldFormat } from '../../types/entity';
import type { ErpRole } from '../../types/user';
import Modal from '../../components/common/Modal';

/* ────────────────────────────────────────────────────────────────
 *  Constants
 * ──────────────────────────────────────────────────────────────── */

/**
 * Entity admin sub-navigation entries matching the monolith's
 * `AdminPageUtils.GetEntityAdminSubNav` toolbar.
 */
const ENTITY_SUB_NAV: ReadonlyArray<{
  id: string;
  label: string;
  pathSuffix: string;
}> = [
  { id: 'details', label: 'Details', pathSuffix: '' },
  { id: 'fields', label: 'Fields', pathSuffix: '/fields' },
  { id: 'relations', label: 'Relations', pathSuffix: '/relations' },
  { id: 'data', label: 'Data', pathSuffix: '/data' },
  { id: 'pages', label: 'Pages', pathSuffix: '/pages' },
  { id: 'web-api', label: 'Web API', pathSuffix: '/web-api' },
];

/**
 * Metadata for each of the 20 user-visible field types.
 * Matches the monolith's `AdminPageUtils.GetFieldCards()` output.
 * RelationField (20) is excluded because relations are managed separately.
 */
interface FieldTypeCardInfo {
  readonly type: FieldType;
  readonly name: string;
  readonly description: string;
  readonly icon: string;
}

const FIELD_TYPE_CARDS: ReadonlyArray<FieldTypeCardInfo> = [
  { type: FieldType.AutoNumberField,    name: 'Auto Number',  description: 'Automatically incremented number', icon: 'fa-sort-numeric-up' },
  { type: FieldType.CheckboxField,      name: 'Checkbox',     description: 'Boolean true/false value',         icon: 'fa-check-square' },
  { type: FieldType.CurrencyField,      name: 'Currency',     description: 'Monetary value with currency',     icon: 'fa-dollar-sign' },
  { type: FieldType.DateField,          name: 'Date',         description: 'Date without time component',      icon: 'fa-calendar' },
  { type: FieldType.DateTimeField,      name: 'Date & Time',  description: 'Date with time component',         icon: 'fa-calendar-alt' },
  { type: FieldType.EmailField,         name: 'Email',        description: 'Valid email address',               icon: 'fa-at' },
  { type: FieldType.FileField,          name: 'File',         description: 'Uploaded file reference',           icon: 'fa-file' },
  { type: FieldType.HtmlField,          name: 'HTML',         description: 'Rich HTML content',                 icon: 'fa-code' },
  { type: FieldType.ImageField,         name: 'Image',        description: 'Image file reference',              icon: 'fa-image' },
  { type: FieldType.MultiLineTextField, name: 'Textarea',     description: 'Multi-line text value',             icon: 'fa-paragraph' },
  { type: FieldType.MultiSelectField,   name: 'Multiselect',  description: 'Multiple choice from list',         icon: 'fa-list' },
  { type: FieldType.NumberField,        name: 'Number',       description: 'Numeric value',                     icon: 'fa-hashtag' },
  { type: FieldType.PasswordField,      name: 'Password',     description: 'Encrypted password value',          icon: 'fa-lock' },
  { type: FieldType.PercentField,       name: 'Percent',      description: 'Percentage value',                  icon: 'fa-percentage' },
  { type: FieldType.PhoneField,         name: 'Phone',        description: 'Phone number',                      icon: 'fa-phone' },
  { type: FieldType.GuidField,          name: 'Unique ID',    description: 'Globally unique identifier',        icon: 'fa-fingerprint' },
  { type: FieldType.SelectField,        name: 'Select',       description: 'Single choice from list',           icon: 'fa-caret-square-down' },
  { type: FieldType.TextField,          name: 'Text',         description: 'Short text value',                  icon: 'fa-font' },
  { type: FieldType.UrlField,           name: 'URL',          description: 'Web address link',                  icon: 'fa-link' },
  { type: FieldType.GeographyField,     name: 'Geography',    description: 'Geospatial coordinates',            icon: 'fa-map-marker-alt' },
];

/* ────────────────────────────────────────────────────────────────
 *  Helpers
 * ──────────────────────────────────────────────────────────────── */

/** Finds the field type card metadata for a given FieldType value. */
function getFieldTypeCard(ft: FieldType): FieldTypeCardInfo {
  return (
    FIELD_TYPE_CARDS.find((c) => c.type === ft) ?? {
      type: ft,
      name: 'Unknown',
      description: 'Unknown field type',
      icon: 'fa-question',
    }
  );
}

/** Human-readable label for a boolean value — matches monolith checked/unchecked. */
function formatBool(val: boolean | null | undefined): string {
  if (val === true) return 'True';
  if (val === false) return 'False';
  return '—';
}

/**
 * Formats `GeographyFieldFormat` enum to a readable label.
 * Maps GeoJSON (1) → "GeoJSON", Text (2) → "Text".
 */
function formatGeographyFormat(val: GeographyFieldFormat | null | undefined): string {
  switch (val) {
    case GeographyFieldFormat.GeoJSON:
      return 'GeoJSON';
    case GeographyFieldFormat.Text:
      return 'Text';
    default:
      return '—';
  }
}

/**
 * Formats a `SelectOption[]` array into a multi-line readable string.
 * Each option is displayed as "value — label" on its own line,
 * mirroring the monolith's textarea rendering of options.
 */
function formatSelectOptions(options: SelectOption[] | null | undefined): string {
  if (!options || options.length === 0) return '—';
  return options.map((o) => `${o.value} — ${o.label}`).join('\n');
}

/** Formats a string array (e.g. multiselect defaults) into comma-separated text. */
function formatStringArray(vals: string[] | null | undefined): string {
  if (!vals || vals.length === 0) return '—';
  return vals.join(', ');
}

/** Formats a CurrencyType into a "CODE — Name" display string. */
function formatCurrency(currency: CurrencyType | null | undefined): string {
  if (!currency) return '—';
  return `${currency.code} — ${currency.name}`;
}

/** Safely displays a value, returning em-dash for null/undefined/empty. */
function displayValue(val: string | number | null | undefined): string {
  if (val === null || val === undefined || val === '') return '—';
  return String(val);
}

/* ────────────────────────────────────────────────────────────────
 *  Read-Only Row Component
 * ──────────────────────────────────────────────────────────────── */

/**
 * Renders a single read-only property row with label/value in a
 * responsive 1/3 + 2/3 grid, matching the sibling details pages.
 */
function ReadOnlyRow({
  label,
  children,
  monospace = false,
}: {
  label: string;
  children: React.ReactNode;
  monospace?: boolean;
}) {
  return (
    <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
      <dt className="text-sm font-semibold text-gray-700">{label}</dt>
      <dd
        className={[
          'text-sm sm:col-span-2',
          monospace ? 'font-mono text-gray-600' : 'text-gray-900',
        ].join(' ')}
      >
        {children}
      </dd>
    </div>
  );
}

/* ────────────────────────────────────────────────────────────────
 *  Component
 * ──────────────────────────────────────────────────────────────── */

/**
 * AdminEntityFieldDetails renders a read-only view of a single
 * entity field with general properties, type-specific settings,
 * an API security permission matrix, and a delete action
 * (disabled for system fields).
 *
 * Replaces the monolith's `field-details.cshtml` Razor Page.
 */
export default function AdminEntityFieldDetails() {
  /* --- Route params ------------------------------------------------ */
  const { entityId = '', fieldId = '' } = useParams<{
    entityId: string;
    fieldId: string;
  }>();

  const navigate = useNavigate();

  /* --- Server state ------------------------------------------------ */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityError,
    error: entityErrorObj,
  } = useEntity(entityId) as {
    data: Entity | undefined;
    isLoading: boolean;
    isError: boolean;
    error: Error | null;
  };

  const {
    mutateAsync: deleteField,
    isPending: isDeleting,
    isError: deleteError,
    error: deleteErrorObj,
    reset: resetDeleteMutation,
  } = useDeleteField();

  const { data: rolesData, isLoading: rolesLoading } = useRoles();

  /* --- Fields fetched independently from /entities/{id}/fields ----- */
  const { data: apiFields, isLoading: fieldsLoading } = useEntityFields(entityId);

  /* --- Local state: delete modal ----------------------------------- */
  const [isDeleteModalVisible, setIsDeleteModalVisible] = useState(false);

  /* --- Derived: locate field from the fields list ------------------ */
  const field = useMemo<AnyField | undefined>(() => {
    if (!fieldId) return undefined;
    // Prefer fields from the dedicated API endpoint; fall back to entity.fields
    const allFields = (apiFields && apiFields.length > 0)
      ? apiFields
      : (entity?.fields ?? []);
    return (allFields as AnyField[]).find((f) => f.id === fieldId);
  }, [apiFields, entity, fieldId]);

  /** Memoised field-type card metadata for the current field. */
  const fieldCard = useMemo<FieldTypeCardInfo>(() => {
    if (!field) {
      return { type: FieldType.TextField, name: 'Unknown', description: '', icon: 'fa-question' };
    }
    return getFieldTypeCard(field.fieldType);
  }, [field]);

  /** Roles array extracted from the ApiResponse wrapper. */
  const roles = useMemo<ErpRole[]>(() => {
    return rolesData?.object ?? [];
  }, [rolesData]);

  /* --- Handlers ---------------------------------------------------- */

  /**
   * Opens the delete confirmation modal.
   * Resets any previous mutation error state before showing.
   */
  const handleOpenDeleteModal = useCallback(() => {
    resetDeleteMutation();
    setIsDeleteModalVisible(true);
  }, [resetDeleteMutation]);

  /** Closes the delete confirmation modal. */
  const handleCloseDeleteModal = useCallback(() => {
    setIsDeleteModalVisible(false);
  }, []);

  /**
   * Confirms field deletion. Replaces the monolith's OnPost() logic:
   *   1. Call EntityManager.DeleteField(ErpEntity.Id, Field.Id)
   *   2. Redirect to entity fields list on success
   *
   * The useDeleteField mutation automatically invalidates entity caches.
   */
  const handleConfirmDelete = useCallback(async () => {
    try {
      await deleteField({ entityId, fieldId });
      setIsDeleteModalVisible(false);
      navigate(`/admin/entities/${entityId}/fields`, { replace: true });
    } catch {
      /* Error state captured by the mutation — displayed in modal. */
    }
  }, [deleteField, entityId, fieldId, navigate]);

  /* --- Loading state ----------------------------------------------- */
  const isLoading = entityLoading || rolesLoading;

  if (isLoading) {
    return (
      <div className="flex min-h-[200px] items-center justify-center">
        <div className="text-center">
          <div
            className="mx-auto mb-4 h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
            role="status"
            aria-label="Loading field data"
          />
          <p className="text-sm text-gray-500">Loading field…</p>
        </div>
      </div>
    );
  }

  /* --- Error state ------------------------------------------------- */
  if (entityError || !entity || !field) {
    const errorMessage =
      entityErrorObj?.message ||
      (!field && entity ? 'Field not found in entity.' : 'Failed to load field data.');

    return (
      <div className="rounded-md border border-red-200 bg-red-50 p-6" role="alert">
        <h2 className="mb-2 text-lg font-semibold text-red-800">
          Error Loading Field
        </h2>
        <p className="text-sm text-red-700">{errorMessage}</p>
        <Link
          to={`/admin/entities/${entityId}/fields`}
          className="mt-4 inline-block text-sm font-medium text-red-700 underline hover:text-red-900"
        >
          ← Back to Fields
        </Link>
      </div>
    );
  }

  /* --- Field permissions shorthand --------------------------------- */
  const permissions: FieldPermissions = field.permissions ?? { canRead: [], canUpdate: [] };

  /* --- Render ------------------------------------------------------ */
  return (
    <div className="mx-auto max-w-screen-xl px-4 py-6">
      {/* ── Page Header / Breadcrumb ────────────────────────────── */}
      <header className="mb-6">
        <nav aria-label="Breadcrumb" className="mb-2">
          <ol className="flex items-center gap-1.5 text-sm text-gray-500">
            <li>
              <Link
                to="/admin/entities"
                className="hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              >
                Entities
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li>
              <Link
                to={`/admin/entities/${entityId}`}
                className="hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              >
                {entity.label || entity.name || 'Entity'}
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li>
              <Link
                to={`/admin/entities/${entityId}/fields`}
                className="hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              >
                Fields
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li className="font-medium text-gray-800" aria-current="page">
              {field.name || 'Field Details'}
            </li>
          </ol>
        </nav>

        <div className="flex items-center gap-3">
          {entity.iconName && (
            <span
              className="flex h-10 w-10 items-center justify-center rounded-md text-white"
              style={{ backgroundColor: entity.color || '#1d4ed8' }}
              aria-hidden="true"
            >
              <i className={`fa fa-${entity.iconName}`} />
            </span>
          )}
          <div>
            <h1 className="text-2xl font-bold text-gray-900">Field Details</h1>
            <p className="text-sm text-gray-500">
              {entity.label || entity.name || ''}
              {' — '}
              {field.label || field.name || ''}
            </p>
          </div>
        </div>
      </header>

      {/* ── Entity Sub-Navigation ─────────────────────────────── */}
      <nav aria-label="Entity sections" className="mb-6 border-b border-gray-200">
        <ul className="flex gap-0" role="tablist">
          {ENTITY_SUB_NAV.map((tab) => {
            const isActive = tab.id === 'fields';
            return (
              <li key={tab.id} role="presentation">
                <Link
                  to={`/admin/entities/${entityId}${tab.pathSuffix}`}
                  role="tab"
                  aria-selected={isActive}
                  className={[
                    'inline-block px-4 py-2.5 text-sm font-medium transition-colors',
                    isActive
                      ? 'border-b-2 border-blue-600 text-blue-600'
                      : 'text-gray-500 hover:border-b-2 hover:border-gray-300 hover:text-gray-700',
                  ]
                    .filter(Boolean)
                    .join(' ')}
                >
                  {tab.label}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>

      {/* ── Action Toolbar ────────────────────────────────────── */}
      <div className="mb-6 flex items-center gap-3">
        <Link
          to={`/admin/entities/${entityId}/fields/${fieldId}/manage`}
          data-testid="edit-field-btn"
          className="inline-flex items-center gap-2 rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
        >
          <i className="fa fa-pencil-alt text-xs" aria-hidden="true" />
          Manage
        </Link>

        {field.system ? (
          <button
            type="button"
            disabled
            className="inline-flex items-center gap-2 rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-400 shadow-sm ring-1 ring-inset ring-gray-200 cursor-not-allowed"
            title="System fields cannot be deleted"
          >
            <i className="fa fa-lock text-xs" aria-hidden="true" />
            Delete Locked
          </button>
        ) : (
          <button
            type="button"
            onClick={handleOpenDeleteModal}
            className="inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:ring-offset-2"
          >
            <i className="fa fa-trash-alt text-xs" aria-hidden="true" />
            Delete Field
          </button>
        )}
      </div>

      {/* ── Field Type Info Card ──────────────────────────────── */}
      <div className="mb-6 inline-flex max-w-xs items-start gap-3 rounded-lg bg-blue-600 p-4 text-white shadow-sm">
        <i className={`fa ${fieldCard.icon} mt-0.5 text-xl`} aria-hidden="true" />
        <div>
          <h2 className="text-base font-semibold">{fieldCard.name}</h2>
          <p className="text-sm text-blue-100">{fieldCard.description}</p>
        </div>
      </div>

      {/* ── General Properties (Read-Only) ────────────────────── */}
      <section
        className="mb-6 rounded-lg border border-gray-200 bg-white shadow-sm"
        aria-labelledby="general-heading"
      >
        <h2
          id="general-heading"
          className="border-b border-gray-200 px-6 py-4 text-lg font-semibold text-gray-900"
        >
          General
        </h2>
        <dl className="divide-y divide-gray-100 px-6">
          <ReadOnlyRow label="Name">{displayValue(field.name)}</ReadOnlyRow>
          <ReadOnlyRow label="Label">{displayValue(field.label)}</ReadOnlyRow>
          <ReadOnlyRow label="Id" monospace>{displayValue(field.id)}</ReadOnlyRow>
          <ReadOnlyRow label="Required">
            {field.required ? (
              <span className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
                Yes
              </span>
            ) : (
              <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
                No
              </span>
            )}
          </ReadOnlyRow>
          <ReadOnlyRow label="Description">{displayValue(field.description)}</ReadOnlyRow>
          <ReadOnlyRow label="Unique">
            {field.unique ? (
              <span className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
                Yes
              </span>
            ) : (
              <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
                No
              </span>
            )}
          </ReadOnlyRow>
          <ReadOnlyRow label="Help Text">{displayValue(field.helpText)}</ReadOnlyRow>
          <ReadOnlyRow label="System">
            {field.system ? (
              <span className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
                Yes
              </span>
            ) : (
              <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
                No
              </span>
            )}
          </ReadOnlyRow>
          <ReadOnlyRow label="Placeholder Text">{displayValue(field.placeholderText)}</ReadOnlyRow>
          <ReadOnlyRow label="Searchable">
            {field.searchable ? (
              <span className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
                Yes
              </span>
            ) : (
              <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
                No
              </span>
            )}
          </ReadOnlyRow>
        </dl>
      </section>

      {/* ── Type-Specific Settings (Read-Only) ────────────────── */}
      <section
        className="mb-6 rounded-lg border border-gray-200 bg-white shadow-sm"
        aria-labelledby="type-specific-heading"
      >
        <h2
          id="type-specific-heading"
          className="border-b border-gray-200 px-6 py-4 text-lg font-semibold text-gray-900"
        >
          {fieldCard.name} Settings
        </h2>
        <dl className="divide-y divide-gray-100 px-6">
          {renderTypeSpecificSection(field)}
        </dl>
      </section>

      {/* ── API Security ──────────────────────────────────────── */}
      <section
        className="mb-6 rounded-lg border border-gray-200 bg-white shadow-sm"
        aria-labelledby="security-heading"
      >
        <h2
          id="security-heading"
          className="border-b border-gray-200 px-6 py-4 text-lg font-semibold text-gray-900"
        >
          API Security
        </h2>
        <div className="px-6 py-4">
          {/* Enable Security checkbox (read-only) */}
          <div className="mb-4 flex items-center gap-2">
            <input
              type="checkbox"
              checked={field.enableSecurity ?? false}
              disabled
              className="h-4 w-4 rounded border-gray-300 text-blue-600"
              id="enable-security-checkbox"
              aria-label="Enable field-level security"
            />
            <label htmlFor="enable-security-checkbox" className="text-sm font-medium text-gray-700">
              Enable Security
            </label>
          </div>

          {/* Permission Matrix (read-only) */}
          {roles.length > 0 && (
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-gray-200 text-sm">
                <thead>
                  <tr>
                    <th
                      scope="col"
                      className="px-4 py-3 text-start font-semibold text-gray-700"
                    >
                      Role
                    </th>
                    <th
                      scope="col"
                      className="px-4 py-3 text-center font-semibold text-gray-700"
                    >
                      CanRead
                    </th>
                    <th
                      scope="col"
                      className="px-4 py-3 text-center font-semibold text-gray-700"
                    >
                      CanUpdate
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {roles.map((role) => (
                    <tr key={role.id}>
                      <td className="px-4 py-3 text-gray-900">{role.name}</td>
                      <td className="px-4 py-3 text-center">
                        <input
                          type="checkbox"
                          checked={permissions.canRead.includes(role.id)}
                          disabled
                          className="h-4 w-4 rounded border-gray-300 text-blue-600"
                          aria-label={`${role.name} can read`}
                        />
                      </td>
                      <td className="px-4 py-3 text-center">
                        <input
                          type="checkbox"
                          checked={permissions.canUpdate.includes(role.id)}
                          disabled
                          className="h-4 w-4 rounded border-gray-300 text-blue-600"
                          aria-label={`${role.name} can update`}
                        />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {roles.length === 0 && !rolesLoading && (
            <p className="text-sm text-gray-500">No roles available to display.</p>
          )}
        </div>
      </section>

      {/* ── Delete Confirmation Modal ─────────────────────────── */}
      <Modal
        isVisible={isDeleteModalVisible}
        title="Delete Field"
        onClose={handleCloseDeleteModal}
      >
        <div className="space-y-4">
          <p className="text-sm text-gray-700">
            Are you sure you want to delete the field{' '}
            <strong className="font-semibold text-gray-900">{field.name}</strong>
            ? This action cannot be undone.
          </p>

          {deleteError && (
            <div
              className="rounded-md border border-red-200 bg-red-50 p-3"
              role="alert"
            >
              <p className="text-sm text-red-700">
                {deleteErrorObj?.message || 'Failed to delete field. Please try again.'}
              </p>
            </div>
          )}

          <div className="flex items-center justify-end gap-3 border-t border-gray-200 pt-4">
            <button
              type="button"
              onClick={handleCloseDeleteModal}
              disabled={isDeleting}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmDelete}
              disabled={isDeleting}
              className="inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {isDeleting && (
                <span
                  className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                  aria-hidden="true"
                />
              )}
              {isDeleting ? 'Deleting…' : 'Confirm Delete'}
            </button>
          </div>
        </div>
      </Modal>
    </div>
  );
}

/* ════════════════════════════════════════════════════════════════
 *  Type-Specific Section Renderer
 *
 *  Switch-cases mirror the monolith's field-details.cshtml
 *  `@switch(Model.Field.GetFieldType())` block.  Every field
 *  type displays the read-only properties appropriate to it.
 * ════════════════════════════════════════════════════════════════ */

/**
 * Renders the type-specific read-only property rows for any of the
 * 20 concrete field types, using the `AnyField` discriminated union.
 */
function renderTypeSpecificSection(field: AnyField): React.ReactNode {
  switch (field.fieldType) {
    /* ── AutoNumberField ──────────────────────────────────────── */
    case FieldType.AutoNumberField:
      return (
        <>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Starting Number">{displayValue(field.startingNumber)}</ReadOnlyRow>
          <ReadOnlyRow label="Display Format">{displayValue(field.displayFormat)}</ReadOnlyRow>
        </>
      );

    /* ── CheckboxField ────────────────────────────────────────── */
    case FieldType.CheckboxField:
      return (
        <ReadOnlyRow label="Default Value">{formatBool(field.defaultValue)}</ReadOnlyRow>
      );

    /* ── CurrencyField ────────────────────────────────────────── */
    case FieldType.CurrencyField:
      return (
        <>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Min Value">{displayValue(field.minValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Max Value">{displayValue(field.maxValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Currency">
            {formatCurrency(field.currency)}
          </ReadOnlyRow>
        </>
      );

    /* ── DateField ────────────────────────────────────────────── */
    case FieldType.DateField:
      return (
        <>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Format">{displayValue(field.format)}</ReadOnlyRow>
          <ReadOnlyRow label="Use Current Time As Default">
            {formatBool(field.useCurrentTimeAsDefaultValue)}
          </ReadOnlyRow>
        </>
      );

    /* ── DateTimeField ────────────────────────────────────────── */
    case FieldType.DateTimeField:
      return (
        <>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Format">{displayValue(field.format)}</ReadOnlyRow>
          <ReadOnlyRow label="Use Current Time As Default">
            {formatBool(field.useCurrentTimeAsDefaultValue)}
          </ReadOnlyRow>
        </>
      );

    /* ── EmailField ───────────────────────────────────────────── */
    case FieldType.EmailField:
      return (
        <>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Max Length">{displayValue(field.maxLength)}</ReadOnlyRow>
        </>
      );

    /* ── FileField ────────────────────────────────────────────── */
    case FieldType.FileField:
      return (
        <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
      );

    /* ── HtmlField ────────────────────────────────────────────── */
    case FieldType.HtmlField:
      return (
        <ReadOnlyRow label="Default Value">
          <pre className="max-h-48 overflow-auto whitespace-pre-wrap rounded-md bg-gray-50 p-3 text-xs text-gray-800">
            {displayValue(field.defaultValue)}
          </pre>
        </ReadOnlyRow>
      );

    /* ── ImageField ───────────────────────────────────────────── */
    case FieldType.ImageField:
      return (
        <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
      );

    /* ── MultiLineTextField ───────────────────────────────────── */
    case FieldType.MultiLineTextField:
      return (
        <ReadOnlyRow label="Default Value">
          <pre className="max-h-48 overflow-auto whitespace-pre-wrap rounded-md bg-gray-50 p-3 text-xs text-gray-800">
            {displayValue(field.defaultValue)}
          </pre>
        </ReadOnlyRow>
      );

    /* ── GeographyField ───────────────────────────────────────── */
    case FieldType.GeographyField:
      return (
        <>
          <ReadOnlyRow label="Format">{formatGeographyFormat(field.format)}</ReadOnlyRow>
          <ReadOnlyRow label="SRID">{displayValue(field.srid)}</ReadOnlyRow>
        </>
      );

    /* ── MultiSelectField ─────────────────────────────────────── */
    case FieldType.MultiSelectField:
      return (
        <>
          <ReadOnlyRow label="Options">
            <pre className="max-h-48 overflow-auto whitespace-pre-wrap rounded-md bg-gray-50 p-3 text-xs text-gray-800">
              {formatSelectOptions(field.options)}
            </pre>
          </ReadOnlyRow>
          <ReadOnlyRow label="Default Values">
            {formatStringArray(field.defaultValue)}
          </ReadOnlyRow>
        </>
      );

    /* ── NumberField ──────────────────────────────────────────── */
    case FieldType.NumberField:
      return (
        <>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Decimal Places">{displayValue(field.decimalPlaces)}</ReadOnlyRow>
          <ReadOnlyRow label="Min Value">{displayValue(field.minValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Max Value">{displayValue(field.maxValue)}</ReadOnlyRow>
        </>
      );

    /* ── PasswordField ────────────────────────────────────────── */
    case FieldType.PasswordField:
      return (
        <>
          <ReadOnlyRow label="Max Length">{displayValue(field.maxLength)}</ReadOnlyRow>
          <ReadOnlyRow label="Encrypted">{formatBool(field.encrypted)}</ReadOnlyRow>
        </>
      );

    /* ── PercentField ─────────────────────────────────────────── */
    case FieldType.PercentField:
      return (
        <>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Decimal Places">{displayValue(field.decimalPlaces)}</ReadOnlyRow>
          <ReadOnlyRow label="Min Value">{displayValue(field.minValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Max Value">{displayValue(field.maxValue)}</ReadOnlyRow>
        </>
      );

    /* ── PhoneField ───────────────────────────────────────────── */
    case FieldType.PhoneField:
      return (
        <>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Max Length">{displayValue(field.maxLength)}</ReadOnlyRow>
        </>
      );

    /* ── GuidField ────────────────────────────────────────────── */
    case FieldType.GuidField:
      return (
        <>
          <ReadOnlyRow label="Default Value" monospace>
            {displayValue(field.defaultValue)}
          </ReadOnlyRow>
          <ReadOnlyRow label="Generate New Id">{formatBool(field.generateNewId)}</ReadOnlyRow>
        </>
      );

    /* ── SelectField ──────────────────────────────────────────── */
    case FieldType.SelectField:
      return (
        <>
          <ReadOnlyRow label="Options">
            <pre className="max-h-48 overflow-auto whitespace-pre-wrap rounded-md bg-gray-50 p-3 text-xs text-gray-800">
              {formatSelectOptions(field.options)}
            </pre>
          </ReadOnlyRow>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
        </>
      );

    /* ── TextField ────────────────────────────────────────────── */
    case FieldType.TextField:
      return (
        <>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Max Length">{displayValue(field.maxLength)}</ReadOnlyRow>
        </>
      );

    /* ── UrlField ─────────────────────────────────────────────── */
    case FieldType.UrlField:
      return (
        <>
          <ReadOnlyRow label="Default Value">{displayValue(field.defaultValue)}</ReadOnlyRow>
          <ReadOnlyRow label="Max Length">{displayValue(field.maxLength)}</ReadOnlyRow>
          <ReadOnlyRow label="Open In New Window">
            {formatBool(field.openTargetInNewWindow)}
          </ReadOnlyRow>
        </>
      );

    /* ── Fallback for unknown / RelationField ─────────────────── */
    default:
      return (
        <ReadOnlyRow label="Field Type">
          <span className="text-gray-500 italic">
            No type-specific settings available for this field type.
          </span>
        </ReadOnlyRow>
      );
  }
}
