/**
 * AdminEntityRelations — Entity Relations List Page.
 *
 * Route: `/admin/entities/:entityId/relations`
 *
 * Full replacement for the monolith's:
 *   - `WebVella.Erp.Plugins.SDK/Pages/entity/relations.cshtml`
 *   - `WebVella.Erp.Plugins.SDK/Pages/entity/relations.cshtml.cs`
 *
 * Displays a paginated, sortable data-table of all entity relations
 * where the current entity participates as either the origin or target.
 * Replicates the monolith's filtering pattern:
 *   `EntityRelationManager.Read().Object
 *     .Where(x => x.TargetEntityId == entity.Id || x.OriginEntityId == entity.Id)`
 *
 * Features:
 * - Page header with entity color/icon/name and action buttons
 * - Entity admin sub-navigation tabs (Details, Fields, Relations, Data, Pages, Web API)
 * - DataTable with 4 columns: action (eye icon), name (with relation type badge +
 *   system lock icon), origin (entity.field), target (entity.field)
 * - Search drawer (550px) with name CONTAINS filter
 * - "Create Relation" button linking to the relation creation form
 * - URL-based filter/sort state via useSearchParams for bookmarkable URLs
 * - Loading and error states with accessible feedback
 *
 * @module pages/admin/AdminEntityRelations
 */

import { useState, useMemo, useCallback } from 'react';
import { useParams, Link, useSearchParams, useNavigate } from 'react-router-dom';

import { useEntity, useRelations } from '../../hooks/useEntities';
import type { Entity, EntityRelation } from '../../types/entity';
import { EntityRelationType } from '../../types/entity';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import TabNav, { TabNavRenderType } from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';

// ---------------------------------------------------------------------------
// Local types
// ---------------------------------------------------------------------------

/**
 * Intersection type that satisfies the DataTable generic constraint
 * (`T extends Record<string, unknown>`) while preserving typed access
 * to all EntityRelation properties in cell renderers and column accessors.
 *
 * The DataTable requires an index-signature-compatible type; EntityRelation
 * as a plain interface does not carry one. This intersection bridges
 * the two without losing type safety on `id`, `name`, or `relationType`.
 */
type RelationRecord = EntityRelation & Record<string, unknown>;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Returns a human-readable badge label for the relation cardinality.
 *
 * Matches the monolith's rendering in relations.cshtml.cs (lines 76-82):
 *   - `EntityRelationType.OneToOne`  → "1 : 1"
 *   - `EntityRelationType.OneToMany` → "1 : N"
 *   - `EntityRelationType.ManyToMany` → "N : N"
 */
function getRelationTypeLabel(type: EntityRelationType): string {
  switch (type) {
    case EntityRelationType.OneToOne:
      return '1 : 1';
    case EntityRelationType.OneToMany:
      return '1 : N';
    case EntityRelationType.ManyToMany:
      return 'N : N';
    default:
      return 'Unknown';
  }
}

/**
 * Builds the entity admin sub-navigation route map.
 *
 * Replaces `AdminPageUtils.GetEntityAdminSubNav(ErpEntity, "relations")` from
 * the SDK plugin's shared utility method.
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
 * Entity Relations list page component.
 *
 * Renders a DataTable of all entity relations involving the current entity,
 * with search/filter drawer, sub-navigation tabs, and CRUD action links.
 *
 * Data flow:
 * 1. `useEntity(entityId)` → entity metadata for header (color, icon, label)
 * 2. `useRelations()` → all relations, filtered client-side by entityId
 * 3. Name CONTAINS filter (URL search-param `name`) applied on filtered set
 * 4. DataTable renders the final filtered/sorted list with pagination
 */
function AdminEntityRelations(): React.JSX.Element {
  /* ================================================================== */
  /*  Route params & navigation                                          */
  /* ================================================================== */
  const { entityId } = useParams<{ entityId: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();

  /* ================================================================== */
  /*  Data fetching                                                      */
  /* ================================================================== */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityError,
  } = useEntity(entityId ?? '');

  const {
    data: allRelations,
    isLoading: relationsLoading,
  } = useRelations();

  /* ================================================================== */
  /*  Local state                                                        */
  /* ================================================================== */

  /** Search drawer visibility toggle. */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);

  /**
   * Name CONTAINS filter value — stored in URL search params for
   * bookmarkable/shareable filter state.
   */
  const nameFilter = searchParams.get('name') ?? '';

  /* ================================================================== */
  /*  Derived data                                                       */
  /* ================================================================== */

  /**
   * Relations involving the current entity as either origin or target.
   *
   * Replicates the C# LINQ filter from relations.cshtml.cs:
   *   `.Where(x => x.TargetEntityId == entity.Id || x.OriginEntityId == entity.Id)`
   */
  const entityRelations = useMemo<EntityRelation[]>(() => {
    if (!allRelations || !entityId) return [];
    return allRelations.filter(
      (r: EntityRelation) =>
        r.originEntityId === entityId || r.targetEntityId === entityId,
    );
  }, [allRelations, entityId]);

  /**
   * Final filtered list after applying the name CONTAINS search.
   *
   * Matches the monolith's string.Contains(StringComparison.InvariantCultureIgnoreCase)
   * pattern from relations.cshtml.cs PageInit().
   */
  const filteredRelations = useMemo<EntityRelation[]>(() => {
    const trimmed = nameFilter.trim().toLowerCase();
    if (!trimmed) return entityRelations;
    return entityRelations.filter((r: EntityRelation) =>
      r.name.toLowerCase().includes(trimmed),
    );
  }, [entityRelations, nameFilter]);

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
   * Update the name CONTAINS filter via URL search params.
   * Uses `replace: true` to avoid polluting the browser history stack
   * with every keystroke.
   */
  const handleFilterChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const value = e.target.value;
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          if (value) {
            next.set('name', value);
          } else {
            next.delete('name');
          }
          return next;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  /**
   * Clear all search filters and close the drawer.
   */
  const handleClearFilters = useCallback(() => {
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.delete('name');
        return next;
      },
      { replace: true },
    );
    setIsDrawerOpen(false);
  }, [setSearchParams]);

  /* ================================================================== */
  /*  Sub-navigation tabs                                                */
  /* ================================================================== */

  /**
   * Entity admin sub-navigation tab configuration.
   *
   * Replaces `AdminPageUtils.GetEntityAdminSubNav(ErpEntity, "relations")`
   * from the SDK plugin. The "relations" tab is the active tab for this page.
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
      if (tabId === 'relations' || !entityId) return;
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
   * Column configuration for the relations DataTable.
   *
   * Matches the monolith's 4-column grid layout from relations.cshtml:
   *   1. action — 1% width, eye icon link to relation details
   *   2. name  — sortable/searchable, includes relation type badge + system lock icon
   *   3. origin — 25% width, "entityName.fieldName" format
   *   4. target — 25% width, "entityName.fieldName" format
   */
  const columns = useMemo<DataTableColumn<RelationRecord>[]>(
    () => [
      /* ── Column 1: Action (eye icon link) ────────────────────── */
      {
        id: 'action',
        label: '',
        width: '1%',
        sortable: false,
        noWrap: true,
        cell: (_value: unknown, record: RelationRecord) => (
          <Link
            to={`/admin/entities/${entityId}/relations/${record.id}`}
            className="inline-flex items-center justify-center text-blue-600 hover:text-blue-800 transition-colors"
            title={`View ${record.name}`}
          >
            <i className="fa fa-eye" aria-hidden="true" />
            <span className="sr-only">View {record.name}</span>
          </Link>
        ),
      },

      /* ── Column 2: Name (with type badge + system lock) ──────── */
      {
        id: 'name',
        name: 'name',
        label: 'Name',
        sortable: true,
        searchable: true,
        accessorKey: 'name',
        cell: (_value: unknown, record: RelationRecord) => (
          <span className="inline-flex items-center gap-2">
            {/* Relation type badge — matches monolith's badge-primary badge-inverse */}
            <span className="inline-flex items-center rounded px-1.5 py-0.5 text-xs font-semibold text-white bg-indigo-900 whitespace-nowrap">
              {getRelationTypeLabel(record.relationType)}
            </span>
            <span className="text-gray-900">{record.name}</span>
            {/* System relation lock icon — matches monolith's fa-lock rendering */}
            {record.system && (
              <i
                className="fa fa-lock text-gray-400 text-xs"
                aria-label="System relation"
                title="System relation"
              />
            )}
          </span>
        ),
      },

      /* ── Column 3: Origin (entity.field) ─────────────────────── */
      {
        id: 'origin',
        label: 'Origin',
        width: '25%',
        sortable: false,
        cell: (_value: unknown, record: RelationRecord) => {
          const entityName = record.originEntityName ?? '';
          const fieldName = record.originFieldName ?? '';
          const display = fieldName ? `${entityName}.${fieldName}` : entityName;
          return <span className="text-gray-700">{display}</span>;
        },
      },

      /* ── Column 4: Target (entity.field) ─────────────────────── */
      {
        id: 'target',
        label: 'Target',
        width: '25%',
        sortable: false,
        cell: (_value: unknown, record: RelationRecord) => {
          const entityName = record.targetEntityName ?? '';
          const fieldName = record.targetFieldName ?? '';
          const display = fieldName ? `${entityName}.${fieldName}` : entityName;
          return <span className="text-gray-700">{display}</span>;
        },
      },
    ],
    [entityId],
  );

  /* ================================================================== */
  /*  Loading state                                                      */
  /* ================================================================== */
  const isLoading = entityLoading || relationsLoading;

  /* ================================================================== */
  /*  Error state                                                        */
  /* ================================================================== */
  if (entityError) {
    return (
      <div className="p-6">
        <div className="rounded-md bg-red-50 p-4" role="alert">
          <p className="text-sm text-red-700">
            Failed to load entity data. Please try again later.
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
        {/* Left: Entity icon + label/subtitle */}
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
            <span className="text-sm text-white/80">Relations</span>
          </div>
        </div>

        {/* Right: Search + Create Relation buttons */}
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={handleDrawerToggle}
            className="inline-flex items-center gap-1.5 rounded px-3 py-1.5 text-sm font-medium text-white bg-white/20 hover:bg-white/30 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white transition-colors"
            aria-label="Open search drawer"
          >
            <i className="fa fa-search" aria-hidden="true" />
            <span>Search</span>
          </button>
          <Link
            to={`/admin/entities/${entityId}/relations/create`}
            className="inline-flex items-center gap-1.5 rounded px-3 py-1.5 text-sm font-medium text-white bg-white/20 hover:bg-white/30 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white transition-colors"
          >
            <i className="fa fa-plus" aria-hidden="true" />
            <span>Create Relation</span>
          </Link>
        </div>
      </header>

      {/* ── Entity Admin Sub-Navigation ─────────────────────────── */}
      <nav
        className="border-b border-gray-200 bg-white px-6"
        aria-label="Entity admin navigation"
      >
        <TabNav
          tabs={entitySubNavTabs}
          activeTabId="relations"
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
              <span className="sr-only">Loading relations…</span>
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

            {/* Relations data table */}
            <DataTable<RelationRecord>
              data={filteredRelations as RelationRecord[]}
              columns={columns}
              totalCount={filteredRelations.length}
              pageSize={1000}
              emptyText="No relations found"
              striped={false}
              hover
              showHeader
              showFooter
              loading={false}
              name="entity-relations"
            />
          </>
        )}
      </main>

      {/* ── Search Drawer ───────────────────────────────────────── */}
      <Drawer
        isVisible={isDrawerOpen}
        title="Search Relations"
        onClose={handleDrawerClose}
        width="550px"
      >
        <div className="p-4 flex flex-col gap-4">
          {/* Name CONTAINS filter */}
          <div>
            <label
              htmlFor="relation-name-filter"
              className="block text-sm font-medium text-gray-700 mb-1"
            >
              Name
            </label>
            <input
              id="relation-name-filter"
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

export default AdminEntityRelations;
