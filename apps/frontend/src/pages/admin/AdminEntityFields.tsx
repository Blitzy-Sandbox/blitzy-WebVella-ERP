/**
 * AdminEntityFields — Entity Fields List Page.
 *
 * Route: `/admin/entities/:entityId/fields`
 *
 * Full replacement for the monolith's:
 *   - `WebVella.Erp.Plugins.SDK/Pages/entity/fields.cshtml`
 *   - `WebVella.Erp.Plugins.SDK/Pages/entity/fields.cshtml.cs`
 *
 * Displays a paginated, sortable data-table of all fields defined on an
 * entity.  Replicates the monolith's entity field list grid with 7 columns:
 * action (eye icon link), name (sortable, searchable), type (badge), system,
 * required, unique, and searchable — plus a 550 px search drawer with a name
 * CONTAINS filter and entity admin sub-navigation tabs.
 *
 * Features:
 * - Page header with entity color/icon/name and action buttons
 * - Entity admin sub-navigation tabs (Details, Fields, Relations, Data,
 *   Pages, Web API)
 * - DataTable with 7 columns matching the monolith's WvGridColumnMeta
 *   definitions from fields.cshtml.cs (lines 103-139)
 * - Search drawer (550 px) with name CONTAINS filter replicating the
 *   wv-drawer from fields.cshtml (lines 63-69)
 * - "Create Field" button linking to the field type selection page
 * - "Search" button toggling the filter drawer
 * - Client-side filtering (case-insensitive CONTAINS) and ascending name
 *   sort matching the monolith's default ordering
 * - Loading and error states with accessible feedback
 *
 * Data flow:
 * 1. `useEntity(entityId)` → entity metadata including `fields` array
 * 2. Name CONTAINS filter (local `useState`) applied on fields
 * 3. Filtered fields sorted by name ascending (monolith default)
 * 4. DataTable renders the final list with pageSize 1000 (all fields visible)
 *
 * @module pages/admin/AdminEntityFields
 */

import { useState, useMemo, useCallback } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';

import { useEntity } from '../../hooks/useEntities';
import type { Entity, Field } from '../../types/entity';
import { FieldType } from '../../types/entity';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import TabNav, { TabNavRenderType } from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';
import Button, { ButtonColor, ButtonSize } from '../../components/common/Button';

// ---------------------------------------------------------------------------
// Local types
// ---------------------------------------------------------------------------

/**
 * Intersection type satisfying the DataTable generic constraint
 * (`T extends Record<string, unknown>`) while preserving typed access
 * to all Field properties in cell renderers and column accessors.
 *
 * The DataTable requires an index-signature-compatible type; `Field`
 * as a plain interface does not carry one.  This intersection bridges
 * the two without losing type safety on `id`, `name`, `fieldType`, etc.
 */
type FieldRecord = Field & Record<string, unknown>;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Returns a human-readable label for the given `FieldType` enum value.
 *
 * Matches the monolith's `@field.GetFieldType().ToString()` rendering
 * in the "type" column of the fields grid (fields.cshtml line 40).
 * Each label corresponds to the C# `FieldType` enum member name with
 * spaces inserted before capitals (e.g. `AutoNumberField` → "Auto Number").
 *
 * @param fieldType - Discriminator value from the `FieldType` const enum.
 * @returns Display-ready label string; "Unknown" for unrecognised values.
 */
function getFieldTypeLabel(fieldType: FieldType): string {
  switch (fieldType) {
    case FieldType.AutoNumberField:
      return 'Auto Number';
    case FieldType.CheckboxField:
      return 'Checkbox';
    case FieldType.CurrencyField:
      return 'Currency';
    case FieldType.DateField:
      return 'Date';
    case FieldType.DateTimeField:
      return 'DateTime';
    case FieldType.EmailField:
      return 'Email';
    case FieldType.FileField:
      return 'File';
    case FieldType.HtmlField:
      return 'HTML';
    case FieldType.ImageField:
      return 'Image';
    case FieldType.MultiLineTextField:
      return 'Multi-Line Text';
    case FieldType.MultiSelectField:
      return 'Multi Select';
    case FieldType.NumberField:
      return 'Number';
    case FieldType.PasswordField:
      return 'Password';
    case FieldType.PercentField:
      return 'Percent';
    case FieldType.PhoneField:
      return 'Phone';
    case FieldType.GuidField:
      return 'GUID';
    case FieldType.SelectField:
      return 'Select';
    case FieldType.TextField:
      return 'Text';
    case FieldType.UrlField:
      return 'URL';
    case FieldType.RelationField:
      return 'Relation';
    case FieldType.GeographyField:
      return 'Geography';
    default:
      return 'Unknown';
  }
}

/**
 * Builds the entity admin sub-navigation route map.
 *
 * Replaces `AdminPageUtils.GetEntityAdminSubNav(ErpEntity, "fields")` from
 * the SDK plugin's shared utility method (fields.cshtml.cs line 187).
 *
 * @param entityId - GUID of the entity whose admin pages are linked.
 * @returns Map of tab ID → route path.
 */
function buildTabRouteMap(entityId: string): Record<string, string> {
  return {
    details: `/admin/entities/${entityId}`,
    fields: `/admin/entities/${entityId}/fields`,
    relations: `/admin/entities/${entityId}/relations`,
    data: `/admin/entities/${entityId}/data`,
    pages: `/admin/entities/${entityId}/pages`,
    'web-api': `/admin/entities/${entityId}/web-api`,
  };
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Entity Fields list page component.
 *
 * Renders a DataTable of all fields belonging to the current entity,
 * with a search/filter drawer, entity admin sub-navigation tabs, and
 * "Create Field" / "Search" header action buttons.
 *
 * Data flow:
 * 1. `useEntity(entityId)` → entity metadata with embedded `fields` array
 * 2. Name CONTAINS filter (local state) applied on the entity's fields
 * 3. Filtered fields sorted by name ascending (monolith default)
 * 4. DataTable renders the final filtered/sorted list with pagination
 */
function AdminEntityFields(): React.JSX.Element {
  /* ================================================================== */
  /*  Route params & navigation                                          */
  /* ================================================================== */
  const { entityId } = useParams<{ entityId: string }>();
  const navigate = useNavigate();

  /* ================================================================== */
  /*  Data fetching                                                      */
  /* ================================================================== */

  /**
   * Fetch entity metadata including the full field list.
   *
   * Replaces the monolith's `EntityManager.ReadEntity(ParentRecordId)` call
   * from fields.cshtml.cs line 62.  The hook uses TanStack Query with a
   * 5-minute staleTime matching the monolith's entity metadata cache.
   */
  const {
    data: entity,
    isLoading,
    isError,
    error,
  } = useEntity(entityId ?? '');

  /* ================================================================== */
  /*  Local state                                                        */
  /* ================================================================== */

  /** Search drawer visibility toggle. */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);

  /**
   * Name CONTAINS filter value — stored in local state.
   * Matches the monolith's wv-filter-text with CONTAINS query type
   * from fields.cshtml line 66.
   */
  const [nameFilter, setNameFilter] = useState('');

  /* ================================================================== */
  /*  Derived data                                                       */
  /* ================================================================== */

  /**
   * Final filtered and sorted field list.
   *
   * Applies the name CONTAINS filter (case-insensitive) matching the
   * monolith's `string.Contains(StringComparison.InvariantCultureIgnoreCase)`
   * pattern from fields.cshtml.cs lines 74-91, then sorts by name ascending
   * matching the monolith's default `order_by=name` / `order_dir=ASC`
   * from fields.cshtml.cs line 93.
   */
  const filteredFields = useMemo<Field[]>(() => {
    if (!entity?.fields) return [];

    let fields = [...entity.fields];

    /* Apply name CONTAINS filter (case-insensitive) */
    const trimmed = nameFilter.trim().toLowerCase();
    if (trimmed) {
      fields = fields.filter((f: Field) =>
        f.name.toLowerCase().includes(trimmed),
      );
    }

    /* Sort by name ascending — monolith default ordering */
    fields.sort((a: Field, b: Field) => a.name.localeCompare(b.name));

    return fields;
  }, [entity?.fields, nameFilter]);

  /* ================================================================== */
  /*  Event handlers                                                     */
  /* ================================================================== */

  /** Toggle search drawer open/closed. */
  const handleDrawerToggle = useCallback(() => {
    setIsDrawerOpen((prev) => !prev);
  }, []);

  /** Close the search drawer. */
  const handleDrawerClose = useCallback(() => {
    setIsDrawerOpen(false);
  }, []);

  /**
   * Update the name CONTAINS filter from the drawer input.
   */
  const handleFilterChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      setNameFilter(e.target.value);
    },
    [],
  );

  /**
   * Clear all search filters and close the drawer.
   */
  const handleClearFilters = useCallback(() => {
    setNameFilter('');
    setIsDrawerOpen(false);
  }, []);

  /* ================================================================== */
  /*  Sub-navigation tabs                                                */
  /* ================================================================== */

  /**
   * Entity admin sub-navigation tab configuration.
   *
   * Replaces `AdminPageUtils.GetEntityAdminSubNav(ErpEntity, "fields")`
   * from the SDK plugin (fields.cshtml.cs line 187).  The "fields" tab
   * is the active tab for this page.
   */
  const entitySubNavTabs = useMemo<TabConfig[]>(
    () => [
      { id: 'details', label: 'Details' },
      { id: 'fields', label: 'Fields' },
      { id: 'relations', label: 'Relations' },
      { id: 'data', label: 'Data' },
      { id: 'pages', label: 'Pages' },
      { id: 'web-api', label: 'Web API' },
    ],
    [],
  );

  /**
   * Navigate to the selected sub-nav tab route.
   * Uses React Router's `navigate()` for SPA-style navigation.
   */
  const handleTabChange = useCallback(
    (tabId: string) => {
      if (tabId === 'fields' || !entityId) return;
      const routeMap = buildTabRouteMap(entityId);
      const href = routeMap[tabId];
      if (href) {
        navigate(href);
      }
    },
    [entityId, navigate],
  );

  /* ================================================================== */
  /*  DataTable column definitions                                       */
  /* ================================================================== */

  /**
   * Column configuration for the fields DataTable.
   *
   * Matches the monolith's 7-column grid layout from fields.cshtml.cs
   * (lines 103-139) and fields.cshtml (lines 26-61):
   *
   *   1. action     — 1% width, eye icon link to field details
   *   2. name       — sortable/searchable, includes system lock icon
   *   3. type       — 120px width, field type badge (indigo)
   *   4. system     — 1% width, check/times icon
   *   5. required   — 1% width, check/times icon
   *   6. unique     — 80px width, check/times icon
   *   7. searchable — 1% width, check/times icon
   */
  const columns = useMemo<DataTableColumn<FieldRecord>[]>(
    () => [
      /* ── Column 1: Action (eye icon link) ────────────────────── */
      {
        id: 'action',
        label: '',
        width: '1%',
        sortable: false,
        cell: (_value: unknown, record: FieldRecord) => (
          <Link
            to={`/admin/entities/${entityId}/fields/${record.id}`}
            className="inline-flex items-center justify-center text-blue-600 hover:text-blue-800 transition-colors"
            title={`View ${record.name}`}
          >
            <i className="fa fa-eye" aria-hidden="true" />
            <span className="sr-only">View {record.name}</span>
          </Link>
        ),
      },

      /* ── Column 2: Name (sortable, searchable, with system lock icon) ── */
      {
        id: 'name',
        name: 'name',
        label: 'Name',
        sortable: true,
        searchable: true,
        accessorKey: 'name',
        cell: (_value: unknown, record: FieldRecord) => (
          <span className="inline-flex items-center gap-2">
            <span className="text-gray-900">{record.name}</span>
            {/* System field lock icon — matches monolith's fa-lock rendering */}
            {record.system && (
              <i
                className="fa fa-lock text-gray-400 text-xs"
                aria-label="System field"
                title="System field"
              />
            )}
          </span>
        ),
      },

      /* ── Column 3: Type (120px, badge) ───────────────────────── */
      {
        id: 'type',
        label: 'Type',
        width: '120px',
        sortable: false,
        cell: (_value: unknown, record: FieldRecord) => (
          <span className="inline-flex items-center rounded px-1.5 py-0.5 text-xs font-semibold text-white bg-indigo-900 whitespace-nowrap">
            {getFieldTypeLabel(record.fieldType)}
          </span>
        ),
      },

      /* ── Column 4: System (1%, check/times icon) ─────────────── */
      {
        id: 'system',
        label: 'System',
        width: '1%',
        sortable: false,
        cell: (_value: unknown, record: FieldRecord) => (
          <span className="inline-flex items-center justify-center">
            {record.system ? (
              <i
                className="fa fa-check text-green-600"
                aria-label="Yes"
                title="System field"
              />
            ) : (
              <i
                className="fa fa-times text-gray-300"
                aria-label="No"
                title="Not a system field"
              />
            )}
          </span>
        ),
      },

      /* ── Column 5: Required (1%, check/times icon) ───────────── */
      {
        id: 'required',
        label: 'Required',
        width: '1%',
        sortable: false,
        cell: (_value: unknown, record: FieldRecord) => (
          <span className="inline-flex items-center justify-center">
            {record.required ? (
              <i
                className="fa fa-check text-green-600"
                aria-label="Yes"
                title="Required"
              />
            ) : (
              <i
                className="fa fa-times text-gray-300"
                aria-label="No"
                title="Not required"
              />
            )}
          </span>
        ),
      },

      /* ── Column 6: Unique (80px, check/times icon) ───────────── */
      {
        id: 'unique',
        label: 'Unique',
        width: '80px',
        sortable: false,
        cell: (_value: unknown, record: FieldRecord) => (
          <span className="inline-flex items-center justify-center">
            {record.unique ? (
              <i
                className="fa fa-check text-green-600"
                aria-label="Yes"
                title="Unique"
              />
            ) : (
              <i
                className="fa fa-times text-gray-300"
                aria-label="No"
                title="Not unique"
              />
            )}
          </span>
        ),
      },

      /* ── Column 7: Searchable (1%, check/times icon) ─────────── */
      {
        id: 'searchable',
        label: 'Searchable',
        width: '1%',
        sortable: false,
        cell: (_value: unknown, record: FieldRecord) => (
          <span className="inline-flex items-center justify-center">
            {record.searchable ? (
              <i
                className="fa fa-check text-green-600"
                aria-label="Yes"
                title="Searchable"
              />
            ) : (
              <i
                className="fa fa-times text-gray-300"
                aria-label="No"
                title="Not searchable"
              />
            )}
          </span>
        ),
      },
    ],
    [entityId],
  );

  /* ================================================================== */
  /*  Error state                                                        */
  /* ================================================================== */

  if (isError) {
    return (
      <div className="p-6">
        <div className="rounded-md bg-red-50 p-4" role="alert">
          <p className="text-sm text-red-700">
            {error?.message ??
              'Failed to load entity data. Please try again later.'}
          </p>
          <Link
            to="/admin/entities"
            className="mt-2 inline-block text-sm font-medium text-red-800 underline hover:text-red-900"
          >
            Back to Entities
          </Link>
        </div>
      </div>
    );
  }

  /* ================================================================== */
  /*  Render                                                             */
  /* ================================================================== */

  return (
    <div className="flex flex-col min-h-full">
      {/* ── Page Header ─────────────────────────────────────────── */}
      <header
        className="flex items-center justify-between px-6 py-4"
        style={{ backgroundColor: entity?.color ?? '#dc3545' }}
      >
        {/* Left: Entity icon + label / subtitle */}
        <div className="flex items-center gap-3">
          {entity?.iconName && (
            <span
              className="inline-flex items-center justify-center h-8 w-8 rounded text-white"
              aria-hidden="true"
            >
              <i className={`fa ${entity.iconName}`} />
            </span>
          )}
          <div>
            <h1 className="text-lg font-semibold text-white leading-tight">
              {entity?.label ?? entity?.name ?? 'Entity'}
            </h1>
            <span className="text-sm text-white/80">Fields</span>
          </div>
        </div>

        {/* Right: Search + Create Field action buttons */}
        <div className="flex items-center gap-2">
          {/*
           * Search toggle button — replaces the monolith's Search button
           * (fields.cshtml.cs line 185) that dispatches
           * WebVella.Erp.Web.Components.PcDrawer open event.
           */}
          <Button
            onClick={handleDrawerToggle}
            iconClass="fa fa-search"
            text="Search"
            color={ButtonColor.White}
            size={ButtonSize.Small}
          />

          {/*
           * Create Field link button — replaces the monolith's anchor
           * (fields.cshtml.cs line 184) linking to
           * /sdk/objects/entity/r/{EntityId}/rl/fields/select-type
           */}
          <Button
            href={`/admin/entities/${entityId}/fields/create`}
            iconClass="fa fa-plus"
            text="Create Field"
            color={ButtonColor.White}
            size={ButtonSize.Small}
          />
        </div>
      </header>

      {/* ── Entity Admin Sub-Navigation ─────────────────────────── */}
      <nav
        className="border-b border-gray-200 bg-white px-6"
        aria-label="Entity admin navigation"
      >
        <TabNav
          tabs={entitySubNavTabs}
          activeTabId="fields"
          onTabChange={handleTabChange}
          renderType={TabNavRenderType.TABS}
          visibleTabs={6}
          bodyClassName="hidden"
        />
      </nav>

      {/* ── Main Content ────────────────────────────────────────── */}
      <main className="flex-1 p-6">
        {isLoading ? (
          /* Loading spinner with accessible announcements */
          <div
            className="flex items-center justify-center py-12"
            aria-live="polite"
            aria-busy="true"
          >
            <div
              className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600"
              role="status"
            >
              <span className="sr-only">Loading fields…</span>
            </div>
          </div>
        ) : (
          <>
            {/* Active filter indicator */}
            {nameFilter.trim() && (
              <div className="mb-4 flex items-center gap-2 text-sm text-gray-600">
                <span>
                  Filtered by name containing:{' '}
                  <strong className="text-gray-900">
                    &ldquo;{nameFilter.trim()}&rdquo;
                  </strong>
                </span>
                <button
                  type="button"
                  onClick={handleClearFilters}
                  className="text-blue-600 hover:text-blue-800 underline text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                >
                  Clear
                </button>
              </div>
            )}

            {/* Fields data table — pageSize 1000 matches monolith's pager_size */}
            <DataTable<FieldRecord>
              data={filteredFields as FieldRecord[]}
              columns={columns}
              totalCount={filteredFields.length}
              pageSize={1000}
              emptyText="No fields found"
              striped={false}
              hover
              showHeader
              showFooter
              loading={false}
              name="entity-fields"
            />
          </>
        )}
      </main>

      {/* ── Search Drawer ───────────────────────────────────────── */}
      <Drawer
        isVisible={isDrawerOpen}
        title="Search Fields"
        onClose={handleDrawerClose}
        width="550px"
        id="search-fields-drawer"
      >
        <div className="p-4 flex flex-col gap-4">
          {/* Name CONTAINS filter — matches monolith's wv-filter-text
              from fields.cshtml line 66 */}
          <div>
            <label
              htmlFor="field-name-filter"
              className="block text-sm font-medium text-gray-700 mb-1"
            >
              Name
            </label>
            <input
              id="field-name-filter"
              type="text"
              value={nameFilter}
              onChange={handleFilterChange}
              placeholder="CONTAINS"
              className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              autoComplete="off"
            />
          </div>

          {/* Clear filters action */}
          <div className="flex justify-end">
            <button
              type="button"
              onClick={handleClearFilters}
              className="inline-flex items-center rounded px-3 py-1.5 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 transition-colors"
            >
              Clear Filters
            </button>
          </div>
        </div>
      </Drawer>
    </div>
  );
}

export default AdminEntityFields;
