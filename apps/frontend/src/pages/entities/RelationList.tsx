/**
 * RelationList — Entity Relations List Page.
 *
 * Route: `/entities/:entityId/relations`
 *
 * Full replacement for the monolith's:
 *   - `WebVella.Erp.Plugins.SDK/Pages/entity/relations.cshtml`
 *   - `WebVella.Erp.Plugins.SDK/Pages/entity/relations.cshtml.cs`
 *
 * Displays all entity relations where the current entity participates as
 * either the origin or target. Replicates the monolith's filtering pattern:
 *   `EntityRelationManager.Read().Object
 *     .Where(x => x.TargetEntityId == entity.Id || x.OriginEntityId == entity.Id)`
 *
 * Features:
 * - Page header with entity color/icon/name and action buttons
 * - Entity admin sub-navigation tabs (Details, Fields, Relations, Views)
 *   with "Relations" active — rendered via Link components
 * - DataTable with 4 columns: action (eye icon link), name (with relation
 *   type badge 1:1/1:N/N:N + system lock icon), origin (entity.field),
 *   target (entity.field)
 * - Search drawer (550px) with name CONTAINS filter (case-insensitive)
 * - "Create Relation" button linking to the relation creation form
 * - Loading, error, and not-found states with accessible feedback
 * - All entities fetched via useEntities() for name resolution fallback
 *   when resolved names are missing from the relation object
 *
 * @module pages/entities/RelationList
 */

import { useState, useMemo, useCallback } from 'react';
import { useParams, Link } from 'react-router-dom';

import { useEntity, useEntities, useRelations } from '../../hooks/useEntities';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import type { Entity, EntityRelation } from '../../types/entity';
import { EntityRelationType } from '../../types/entity';

// ---------------------------------------------------------------------------
// Local types
// ---------------------------------------------------------------------------

/**
 * Intersection type that satisfies the DataTable generic constraint
 * (`T extends Record<string, unknown>`) while preserving typed access
 * to all EntityRelation properties in cell renderers and column accessors.
 *
 * DataTable requires an index-signature-compatible row type; EntityRelation
 * as a plain interface does not carry one. This intersection bridges the
 * two without losing type safety on `id`, `name`, or `relationType`.
 */
type RelationRecord = EntityRelation & Record<string, unknown>;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Entity admin sub-navigation tab definitions.
 *
 * Replaces `AdminPageUtils.GetEntityAdminSubNav(ErpEntity, "relations")`
 * from the SDK plugin. Each tab maps to a sub-route under `/entities/:entityId`.
 */
const ADMIN_SUB_NAV_TABS = [
  { id: 'details', label: 'Details', path: '' },
  { id: 'fields', label: 'Fields', path: '/fields' },
  { id: 'relations', label: 'Relations', path: '/relations' },
  { id: 'views', label: 'Views', path: '/views' },
] as const;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Returns a human-readable badge label for the relation cardinality.
 *
 * Matches the monolith's rendering in relations.cshtml.cs (lines 76-82):
 *   - `EntityRelationType.OneToOne`   → "1 : 1"
 *   - `EntityRelationType.OneToMany`  → "1 : N"
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
 * Resolves an entity name by ID from the full entities array.
 *
 * Used as a fallback when the relation's resolved `originEntityName` or
 * `targetEntityName` is empty or undefined (e.g., if the Entity Management
 * API did not enrich the response).
 *
 * @param entityId - GUID of the entity to look up
 * @param entities - Full list of entities from useEntities()
 * @returns The entity's name or an empty string if not found
 */
function resolveEntityName(entityId: string, entities: Entity[] | undefined): string {
  if (!entities || !entityId) return '';
  const entity = entities.find((e) => e.id === entityId);
  return entity?.name ?? '';
}

/**
 * Resolves a field name by field ID within a given entity.
 *
 * Falls back to looking up the field from the entity's `fields` array when
 * the relation's resolved `originFieldName` or `targetFieldName` is missing.
 *
 * @param entityId - GUID of the entity that owns the field
 * @param fieldId - GUID of the field to look up
 * @param entities - Full list of entities from useEntities()
 * @returns The field's name or an empty string if not found
 */
function resolveFieldName(
  entityId: string,
  fieldId: string,
  entities: Entity[] | undefined,
): string {
  if (!entities || !entityId || !fieldId) return '';
  const entity = entities.find((e) => e.id === entityId);
  if (!entity?.fields) return '';
  const field = entity.fields.find((f) => f.id === fieldId);
  return field?.name ?? '';
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Entity Relations list page component.
 *
 * Renders a DataTable of all entity relations involving the current entity,
 * with a search/filter drawer, sub-navigation tabs, and CRUD action links.
 *
 * Data flow:
 * 1. `useEntity(entityId)` → entity metadata for header (color, icon, label)
 * 2. `useRelations()` → all relations, filtered client-side by entityId
 * 3. `useEntities()` → all entities for name resolution fallback
 * 4. Name CONTAINS filter (local state) applied on the filtered set
 * 5. DataTable renders the final filtered/sorted list with pagination
 */
function RelationList(): React.JSX.Element {
  /* ================================================================== */
  /*  Route params                                                       */
  /* ================================================================== */
  const { entityId } = useParams<{ entityId: string }>();
  const safeEntityId = entityId ?? '';

  /* ================================================================== */
  /*  Data fetching                                                      */
  /* ================================================================== */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityError,
  } = useEntity(safeEntityId);

  const {
    data: allRelations,
    isLoading: relationsLoading,
  } = useRelations();

  const {
    data: allEntities,
  } = useEntities();

  /* ================================================================== */
  /*  Local state                                                        */
  /* ================================================================== */

  /** Search drawer visibility toggle. */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);

  /**
   * Name CONTAINS filter value — stored in local state.
   * Matches the monolith's query-string `name` parameter behaviour
   * from relations.cshtml.cs PageInit().
   */
  const [nameFilter, setNameFilter] = useState('');

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
    if (!allRelations || !safeEntityId) return [];
    return allRelations.filter(
      (r: EntityRelation) =>
        r.originEntityId === safeEntityId || r.targetEntityId === safeEntityId,
    );
  }, [allRelations, safeEntityId]);

  /**
   * Final filtered list after applying the name CONTAINS search.
   *
   * Matches the monolith's `string.Contains(StringComparison.InvariantCultureIgnoreCase)`
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
   * Update the name CONTAINS filter from the text input.
   */
  const handleFilterChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      setNameFilter(e.target.value);
    },
    [],
  );

  /**
   * Clear all search filters. Resets the name input and closes the drawer.
   * Replaces the monolith's "clear all" title-action link in the search drawer.
   */
  const handleClearFilters = useCallback(() => {
    setNameFilter('');
    setIsDrawerOpen(false);
  }, []);

  /* ================================================================== */
  /*  DataTable column definitions                                       */
  /* ================================================================== */

  /**
   * Column configuration for the relations DataTable.
   *
   * Matches the monolith's 4-column grid layout from relations.cshtml:
   *   1. action — 1% width, eye icon link to relation details
   *   2. name  — sortable/searchable, includes relation type badge + system lock icon
   *   3. origin — 25% width, stacked "Entity: {name}" + "Field: {name}"
   *   4. target — 25% width, stacked "Entity: {name}" + "Field: {name}"
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
            to={`/entities/${safeEntityId}/relations/${record.id}`}
            className="inline-flex items-center justify-center text-gray-400 hover:text-blue-600 transition-colors"
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
          <span className="inline-flex items-center gap-2 flex-wrap">
            {/* Relation type badge — matches monolith's badge-primary badge-inverse */}
            <span className="inline-flex items-center rounded px-1.5 py-0.5 text-xs font-semibold text-white bg-indigo-900 whitespace-nowrap">
              {getRelationTypeLabel(record.relationType)}
            </span>
            <span className="font-semibold text-blue-700">
              {record.name}
            </span>
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

      /* ── Column 3: Origin (entity + field) ───────────────────── */
      {
        id: 'origin',
        label: 'Origin',
        width: '25%',
        sortable: false,
        cell: (_value: unknown, record: RelationRecord) => {
          const entityName =
            record.originEntityName || resolveEntityName(record.originEntityId, allEntities);
          const fieldName =
            record.originFieldName || resolveFieldName(record.originEntityId, record.originFieldId, allEntities);

          return (
            <div className="flex flex-col text-sm leading-relaxed">
              <span>
                <span className="font-semibold text-gray-500">Entity: </span>
                <span className="text-gray-900">{entityName || '—'}</span>
              </span>
              <span>
                <span className="font-semibold text-gray-500">Field: </span>
                <span className="text-gray-900">{fieldName || '—'}</span>
              </span>
            </div>
          );
        },
      },

      /* ── Column 4: Target (entity + field) ───────────────────── */
      {
        id: 'target',
        label: 'Target',
        width: '25%',
        sortable: false,
        cell: (_value: unknown, record: RelationRecord) => {
          const entityName =
            record.targetEntityName || resolveEntityName(record.targetEntityId, allEntities);
          const fieldName =
            record.targetFieldName || resolveFieldName(record.targetEntityId, record.targetFieldId, allEntities);

          return (
            <div className="flex flex-col text-sm leading-relaxed">
              <span>
                <span className="font-semibold text-gray-500">Entity: </span>
                <span className="text-gray-900">{entityName || '—'}</span>
              </span>
              <span>
                <span className="font-semibold text-gray-500">Field: </span>
                <span className="text-gray-900">{fieldName || '—'}</span>
              </span>
            </div>
          );
        },
      },
    ],
    [safeEntityId, allEntities],
  );

  /* ================================================================== */
  /*  Computed state                                                     */
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
            Failed to load entity data. The entity may not exist or the server
            may be unavailable.
          </p>
          <Link
            to="/entities"
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
              <i className={entity.iconName} />
            </span>
          )}
          <div>
            <h1 className="text-lg font-semibold text-white leading-tight">
              {entity?.label ?? entity?.name ?? 'Entity'}
            </h1>
            <span className="text-sm text-white/80">Relations</span>
          </div>
        </div>

        {/* Right: Search + Create Relation action buttons */}
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
            to={`/entities/${safeEntityId}/relations/create`}
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
        <ul className="flex gap-0 -mb-px" role="tablist">
          {ADMIN_SUB_NAV_TABS.map((tab) => {
            const isActive = tab.id === 'relations';
            const href = `/entities/${safeEntityId}${tab.path}`;
            return (
              <li key={tab.id} role="presentation">
                <Link
                  to={href}
                  role="tab"
                  aria-selected={isActive}
                  aria-current={isActive ? 'page' : undefined}
                  className={[
                    'inline-block px-4 py-3 text-sm font-medium border-b-2 transition-colors',
                    isActive
                      ? 'border-blue-600 text-blue-600'
                      : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300',
                  ].join(' ')}
                >
                  {tab.label}
                </Link>
              </li>
            );
          })}
        </ul>
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
              name="entity-relations"
            />
          </>
        )}
      </main>

      {/* ── Search Drawer ───────────────────────────────────────── */}
      <Drawer
        isVisible={isDrawerOpen}
        title="Search Relations"
        titleAction={
          <button
            type="button"
            onClick={handleClearFilters}
            className="text-sm text-blue-600 hover:text-blue-800 underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            clear all
          </button>
        }
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

export default RelationList;
