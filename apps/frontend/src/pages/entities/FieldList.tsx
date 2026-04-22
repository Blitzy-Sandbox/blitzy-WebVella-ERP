/**
 * FieldList — Entity Fields List Page
 *
 * Displays all fields defined on a specific entity in a sortable, filterable
 * data table with field-type badges, boolean indicator columns, and a search
 * drawer for name filtering.
 *
 * Route: `/entities/:entityId/fields`
 *
 * Replaces:
 *   - WebVella.Erp.Plugins.SDK/Pages/entity/fields.cshtml
 *   - WebVella.Erp.Plugins.SDK/Pages/entity/fields.cshtml.cs (FieldsModel)
 *
 * Source mapping:
 *   - FieldsModel.OnGet()          → useEntity(entityId) TanStack Query hook
 *   - allFields CONTAINS filter    → useMemo client-side nameFilter
 *   - allFields OrderBy(x=>x.Name) → useMemo client-side sort
 *   - wv-grid (7 columns)          → DataTable with 7 DataTableColumn defs
 *   - wv-drawer (550px, "Search")  → Drawer component with name text input
 *   - Entity admin sub-nav tabs    → inline tab Links component
 *   - Create Field header action   → Link to /entities/:entityId/fields/create
 *
 * @module FieldList
 */

import {
  useState,
  useMemo,
  useCallback,
  type ReactElement,
  type ChangeEvent,
} from 'react';
import { useParams, Link } from 'react-router-dom';
import { useEntity, useEntityFields } from '../../hooks/useEntities';
import {
  DataTable,
  type DataTableColumn,
} from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import type { Entity, AnyField, FieldType } from '../../types/entity';

/**
 * Widened field type that satisfies DataTable's `Record<string, unknown>`
 * generic constraint while preserving full AnyField property access in
 * column cell renderers.
 *
 * At runtime every JavaScript object satisfies an index signature, but
 * TypeScript interfaces do not carry one by default.  The intersection
 * adds it without losing named-property autocompletion.
 */
type FieldRow = AnyField & Record<string, unknown>;

/* =========================================================================
   Field Type Visual Metadata
   ========================================================================= */

/**
 * Visual configuration for rendering field-type badges in the DataTable.
 * Each field type receives a distinct colour and human-readable label,
 * mirroring AdminPageUtils.GetFieldCards() from the C# monolith.
 */
interface FieldTypeMeta {
  /** Human-readable field type name (e.g. "Auto Number"). */
  label: string;
  /** Tailwind background colour class for the badge. */
  bgClass: string;
  /** Tailwind text colour class for badge text. */
  textClass: string;
}

/**
 * Maps FieldType const-enum numeric values to visual metadata.
 *
 * Colour assignments mirror the monolith's AdminPageUtils.GetFieldCards():
 *   Purple → AutoNumber(1), Image(9)
 *   Orange → Checkbox(2)
 *   Green  → Currency(3), Percent(14)
 *   Blue   → Date(4), DateTime(5), MultiLineText(10), Number(12), Text(18)
 *   Teal   → Email(6), Phone(15), Url(19)
 *   Gray   → File(7), Guid(16)
 *   Slate  → Html(8)
 *   Indigo → MultiSelect(11), Select(17)
 *   Red    → Password(13), Geography(21)
 *   Amber  → Relation(20) — internal/virtual type
 */
const FIELD_TYPE_META: Record<number, FieldTypeMeta> = {
  /* 1  AutoNumberField  */ 1: {
    label: 'Auto Number',
    bgClass: 'bg-purple-100',
    textClass: 'text-purple-700',
  },
  /* 2  CheckboxField    */ 2: {
    label: 'Checkbox',
    bgClass: 'bg-orange-100',
    textClass: 'text-orange-700',
  },
  /* 3  CurrencyField    */ 3: {
    label: 'Currency',
    bgClass: 'bg-green-100',
    textClass: 'text-green-700',
  },
  /* 4  DateField        */ 4: {
    label: 'Date',
    bgClass: 'bg-blue-100',
    textClass: 'text-blue-700',
  },
  /* 5  DateTimeField    */ 5: {
    label: 'Date Time',
    bgClass: 'bg-blue-100',
    textClass: 'text-blue-700',
  },
  /* 6  EmailField       */ 6: {
    label: 'Email',
    bgClass: 'bg-teal-100',
    textClass: 'text-teal-700',
  },
  /* 7  FileField        */ 7: {
    label: 'File',
    bgClass: 'bg-gray-100',
    textClass: 'text-gray-700',
  },
  /* 8  HtmlField        */ 8: {
    label: 'Html',
    bgClass: 'bg-slate-200',
    textClass: 'text-slate-800',
  },
  /* 9  ImageField       */ 9: {
    label: 'Image',
    bgClass: 'bg-purple-100',
    textClass: 'text-purple-700',
  },
  /* 10 MultiLineTextField */ 10: {
    label: 'Textarea',
    bgClass: 'bg-blue-100',
    textClass: 'text-blue-700',
  },
  /* 11 MultiSelectField */ 11: {
    label: 'Multiselect',
    bgClass: 'bg-indigo-100',
    textClass: 'text-indigo-700',
  },
  /* 12 NumberField      */ 12: {
    label: 'Number',
    bgClass: 'bg-blue-100',
    textClass: 'text-blue-700',
  },
  /* 13 PasswordField    */ 13: {
    label: 'Password',
    bgClass: 'bg-red-100',
    textClass: 'text-red-700',
  },
  /* 14 PercentField     */ 14: {
    label: 'Percent',
    bgClass: 'bg-green-100',
    textClass: 'text-green-700',
  },
  /* 15 PhoneField       */ 15: {
    label: 'Phone',
    bgClass: 'bg-teal-100',
    textClass: 'text-teal-700',
  },
  /* 16 GuidField        */ 16: {
    label: 'Guid',
    bgClass: 'bg-gray-100',
    textClass: 'text-gray-700',
  },
  /* 17 SelectField      */ 17: {
    label: 'Select',
    bgClass: 'bg-indigo-100',
    textClass: 'text-indigo-700',
  },
  /* 18 TextField        */ 18: {
    label: 'Text',
    bgClass: 'bg-blue-100',
    textClass: 'text-blue-700',
  },
  /* 19 UrlField         */ 19: {
    label: 'Url',
    bgClass: 'bg-teal-100',
    textClass: 'text-teal-700',
  },
  /* 20 RelationField    */ 20: {
    label: 'Relation',
    bgClass: 'bg-amber-100',
    textClass: 'text-amber-700',
  },
  /* 21 GeographyField   */ 21: {
    label: 'Geography',
    bgClass: 'bg-red-100',
    textClass: 'text-red-700',
  },
};

/** Fallback metadata for unrecognised field types — defensive guard. */
const DEFAULT_FIELD_META: FieldTypeMeta = {
  label: 'Unknown',
  bgClass: 'bg-gray-100',
  textClass: 'text-gray-600',
};

/**
 * Returns visual metadata for a given FieldType numeric value.
 * Falls back to DEFAULT_FIELD_META for unrecognised values.
 */
function getFieldTypeMeta(fieldType: FieldType | number): FieldTypeMeta {
  return FIELD_TYPE_META[fieldType] ?? DEFAULT_FIELD_META;
}

/* =========================================================================
   Inline SVG Icon Components
   ========================================================================= */

/**
 * Eye icon — navigates to the field detail view.
 * Replaces the monolith's "fa fa-eye" in the wv-grid action column.
 */
function EyeIcon(): ReactElement {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      className="inline-block h-4 w-4"
      aria-hidden="true"
    >
      <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  );
}

/**
 * Plus icon — used in the "Create Field" action button.
 * Replaces the monolith's "fa fa-plus" in header actions.
 */
function PlusIcon(): ReactElement {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      className="inline-block h-4 w-4"
      aria-hidden="true"
    >
      <line x1="12" y1="5" x2="12" y2="19" />
      <line x1="5" y1="12" x2="19" y2="12" />
    </svg>
  );
}

/**
 * Magnifying-glass icon — used in the "Search" drawer toggle button.
 * Replaces the monolith's "fa fa-search" in header actions.
 */
function SearchIcon(): ReactElement {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      className="inline-block h-4 w-4"
      aria-hidden="true"
    >
      <circle cx="11" cy="11" r="8" />
      <line x1="21" y1="21" x2="16.65" y2="16.65" />
    </svg>
  );
}

/* =========================================================================
   Presentational Helper Components
   ========================================================================= */

/**
 * Renders a coloured pill badge displaying the field type label.
 * Colour & label come from the FIELD_TYPE_META lookup map.
 */
function FieldTypeBadge({
  fieldType,
}: {
  fieldType: FieldType;
}): ReactElement {
  const meta = getFieldTypeMeta(fieldType);
  return (
    <span
      className={`inline-flex items-center rounded-md px-2 py-0.5 text-xs font-medium ${meta.bgClass} ${meta.textClass}`}
    >
      {meta.label}
    </span>
  );
}

/**
 * Boolean indicator badge — green "Yes" / gray "No".
 * Used for the system, required, unique, and searchable DataTable columns.
 */
function BooleanBadge({ value }: { value: boolean }): ReactElement {
  return value ? (
    <span className="inline-flex items-center rounded-full bg-green-100 px-2 py-0.5 text-xs font-medium text-green-700">
      Yes
    </span>
  ) : (
    <span className="inline-flex items-center rounded-full bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-500">
      No
    </span>
  );
}

/* =========================================================================
   Main Page Component
   ========================================================================= */

/**
 * FieldList page component — displays all fields for a specific entity.
 *
 * Implements the complete workflow from the monolith's FieldsModel:
 *   - Loads entity by ID         → useEntity(entityId)
 *   - PagerSize=1000 (all)       → pageSize=0 (client-side, no pagination)
 *   - Name CONTAINS filter       → useState + useMemo
 *   - OrderBy(x => x.Name)       → useMemo sort
 *   - 7-column wv-grid           → DataTable with 7 DataTableColumn defs
 *   - wv-drawer "Search Fields"  → Drawer (550px)
 *   - Header "Create Field"      → Link to /entities/:entityId/fields/create
 *   - Admin sub-nav tabs         → 3 tab Links (Details / Fields / Relations)
 */
export default function FieldList(): ReactElement {
  /* ── Route parameters ─────────────────────────────────────── */
  const { entityId = '' } = useParams<{ entityId: string }>();

  /* ── Entity data (TanStack Query) ─────────────────────────── */
  const { data: entity, isLoading: entityLoading, isError, error } = useEntity(entityId);

  /* ── Fields data (fetched independently from /entities/{id}/fields) ── */
  const { data: apiFields, isLoading: fieldsLoading } = useEntityFields(entityId);

  const isLoading = entityLoading || fieldsLoading;

  /* ── Local UI state ───────────────────────────────────────── */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);
  const [nameFilter, setNameFilter] = useState('');

  /* ── Filtered & sorted fields (memoised) ──────────────────── */
  const filteredFields = useMemo((): AnyField[] => {
    // Prefer fields from the dedicated API endpoint; fall back to entity.fields
    const rawFields = (apiFields && apiFields.length > 0)
      ? apiFields
      : (entity?.fields ?? []) as AnyField[];

    if (rawFields.length === 0) return [];

    let fields = [...rawFields];

    // Case-insensitive CONTAINS filter on field name.
    // Mirrors: allFields.FindAll(x => x.Name.ToLowerInvariant().Contains(...))
    if (nameFilter.trim()) {
      const lower = nameFilter.trim().toLowerCase();
      fields = fields.filter((f) => f.name.toLowerCase().includes(lower));
    }

    // Sort ascending by name — mirrors: allFields.OrderBy(x => x.Name)
    fields.sort((a, b) => a.name.localeCompare(b.name));
    return fields;
  }, [entity?.fields, nameFilter]);

  /* ── Callbacks (stable references prevent child re-renders) ── */
  const handleDrawerToggle = useCallback(() => {
    setIsDrawerOpen((prev) => !prev);
  }, []);

  const handleDrawerClose = useCallback(() => {
    setIsDrawerOpen(false);
  }, []);

  const handleFilterChange = useCallback(
    (event: ChangeEvent<HTMLInputElement>) => {
      setNameFilter(event.target.value);
    },
    [],
  );

  const handleFilterClear = useCallback(() => {
    setNameFilter('');
  }, []);

  /* ── DataTable columns (7 columns matching monolith grid) ─── */
  const columns = useMemo(
    (): DataTableColumn<FieldRow>[] => [
      /* 1. Action — eye icon link to field detail */
      {
        id: 'action',
        label: '',
        width: '1%',
        cell: (_value: unknown, record: FieldRow) => (
          <Link
            to={`/entities/${entityId}/fields/${record.id}`}
            className="inline-flex text-blue-600 hover:text-blue-800"
            title={`View field ${record.name}`}
          >
            <EyeIcon />
          </Link>
        ),
      },
      /* 2. Name — sortable text */
      {
        id: 'name',
        label: 'Name',
        accessorKey: 'name',
        sortable: true,
      },
      /* 3. Type — field type colour badge (120px) */
      {
        id: 'type',
        label: 'Type',
        width: '120px',
        accessorKey: 'fieldType',
        cell: (_value: unknown, record: FieldRow) => (
          <FieldTypeBadge fieldType={record.fieldType} />
        ),
      },
      /* 4. System — boolean badge (1%) */
      {
        id: 'system',
        label: 'System',
        width: '1%',
        accessorKey: 'system',
        cell: (value: unknown) => <BooleanBadge value={Boolean(value)} />,
      },
      /* 5. Required — boolean badge (1%) */
      {
        id: 'required',
        label: 'Required',
        width: '1%',
        accessorKey: 'required',
        cell: (value: unknown) => <BooleanBadge value={Boolean(value)} />,
      },
      /* 6. Unique — boolean badge (80px) */
      {
        id: 'unique',
        label: 'Unique',
        width: '80px',
        accessorKey: 'unique',
        cell: (value: unknown) => <BooleanBadge value={Boolean(value)} />,
      },
      /* 7. Searchable — boolean badge (1%) */
      {
        id: 'searchable',
        label: 'Searchable',
        width: '1%',
        accessorKey: 'searchable',
        cell: (value: unknown) => <BooleanBadge value={Boolean(value)} />,
      },
    ],
    [entityId],
  );

  // ── Loading state ──────────────────────────────────────────
  if (isLoading) {
    return (
      <div
        className="flex min-h-[50vh] items-center justify-center"
        role="status"
      >
        <div className="flex flex-col items-center gap-3">
          <div
            className="h-8 w-8 animate-spin rounded-full border-4 border-blue-200 border-t-blue-600"
            aria-hidden="true"
          />
          <span className="text-sm text-gray-500">
            Loading entity fields…
          </span>
        </div>
      </div>
    );
  }

  // ── Error state ────────────────────────────────────────────
  if (isError) {
    return (
      <div
        className="flex min-h-[50vh] items-center justify-center"
        role="alert"
      >
        <div className="max-w-md rounded-lg border border-red-200 bg-red-50 p-6 text-center">
          <h2 className="mb-2 text-lg font-semibold text-red-800">
            Failed to Load Fields
          </h2>
          <p className="text-sm text-red-600">
            {error?.message ??
              'An unexpected error occurred while loading entity fields.'}
          </p>
          <Link
            to="/entities"
            className="mt-4 inline-block text-sm font-medium text-blue-600 hover:text-blue-800"
          >
            ← Back to Entities
          </Link>
        </div>
      </div>
    );
  }

  // ── Not-found state ────────────────────────────────────────
  if (!entity) {
    return (
      <div
        className="flex min-h-[50vh] items-center justify-center"
        role="alert"
      >
        <div className="max-w-md rounded-lg border border-amber-200 bg-amber-50 p-6 text-center">
          <h2 className="mb-2 text-lg font-semibold text-amber-800">
            Entity Not Found
          </h2>
          <p className="text-sm text-amber-600">
            The entity you are looking for does not exist or has been removed.
          </p>
          <Link
            to="/entities"
            className="mt-4 inline-block text-sm font-medium text-blue-600 hover:text-blue-800"
          >
            ← Back to Entities
          </Link>
        </div>
      </div>
    );
  }

  // ── Sub-navigation tab definitions ─────────────────────────
  const subNavTabs = [
    { label: 'Details', to: `/entities/${entityId}`, active: false },
    { label: 'Fields', to: `/entities/${entityId}/fields`, active: true },
    {
      label: 'Relations',
      to: `/entities/${entityId}/relations`,
      active: false,
    },
  ];

  return (
    <div className="flex flex-col gap-6 p-6">
      {/* ── Page Header ──────────────────────────────────── */}
      <div className="flex flex-col gap-4">
        {/* Title row + action buttons */}
        <div className="flex flex-wrap items-center justify-between gap-4">
          <div className="flex items-center gap-3">
            {entity.iconName ? (
              <span
                className="flex h-10 w-10 items-center justify-center rounded-lg text-lg text-white"
                style={{
                  backgroundColor: entity.color || '#6b7280',
                }}
                aria-hidden="true"
              >
                <i className={entity.iconName} />
              </span>
            ) : null}
            <div>
              <h1 className="text-xl font-bold text-gray-900">
                {entity.name}
              </h1>
              <p className="text-sm text-gray-500">
                {filteredFields.length}{' '}
                {filteredFields.length === 1 ? 'field' : 'fields'}
                {nameFilter.trim() ? ' (filtered)' : ''}
              </p>
            </div>
          </div>

          {/* Action buttons */}
          <div className="flex items-center gap-2">
            <Link
              to={`/entities/${entityId}/fields/create`}
              className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              <PlusIcon />
              Create Field
            </Link>
            <button
              type="button"
              onClick={handleDrawerToggle}
              className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              aria-label="Toggle search fields drawer"
            >
              <SearchIcon />
              Search
            </button>
          </div>
        </div>

        {/* ── Entity Admin Sub-Navigation ────────────────── */}
        <nav
          aria-label="Entity administration"
          className="border-b border-gray-200"
        >
          <ul className="-mb-px flex gap-0" role="tablist">
            {subNavTabs.map((tab) => (
              <li key={tab.label} role="presentation">
                <Link
                  to={tab.to}
                  role="tab"
                  aria-selected={tab.active}
                  aria-current={tab.active ? 'page' : undefined}
                  className={
                    tab.active
                      ? 'inline-block border-b-2 border-blue-600 px-4 py-2 text-sm font-medium text-blue-600'
                      : 'inline-block border-b-2 border-transparent px-4 py-2 text-sm font-medium text-gray-500 hover:border-gray-300 hover:text-gray-700'
                  }
                >
                  {tab.label}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
      </div>

      {/* ── Data Table ───────────────────────────────────── */}
      <DataTable<FieldRow>
        data={filteredFields as FieldRow[]}
        columns={columns}
        pageSize={0}
        striped
        hover
        responsiveBreakpoint="lg"
        emptyText={
          nameFilter.trim()
            ? 'No fields match the current filter.'
            : 'No fields defined for this entity.'
        }
      />

      {/* ── Search Drawer ────────────────────────────────── */}
      <Drawer
        isVisible={isDrawerOpen}
        width="550px"
        title="Search Fields"
        onClose={handleDrawerClose}
        titleAction={
          nameFilter.trim() ? (
            <button
              type="button"
              onClick={handleFilterClear}
              className="text-sm font-medium text-blue-600 hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              clear all
            </button>
          ) : null
        }
      >
        <div className="flex flex-col gap-4 p-4">
          <div>
            <label
              htmlFor="field-name-filter"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Name
            </label>
            <input
              id="field-name-filter"
              type="text"
              value={nameFilter}
              onChange={handleFilterChange}
              placeholder="Filter by field name…"
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus:outline-none"
              autoComplete="off"
            />
            <p className="mt-1 text-xs text-gray-500">
              Case-insensitive contains filter
            </p>
          </div>
        </div>
      </Drawer>
    </div>
  );
}
