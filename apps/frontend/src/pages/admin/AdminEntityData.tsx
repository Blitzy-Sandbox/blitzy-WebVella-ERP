/**
 * Admin Entity Data — Record Listing Page
 *
 * Replaces `WebVella.Erp.Plugins.SDK/Pages/entity/data.cshtml[.cs]`.
 *
 * Route: `/admin/entities/:entityId/data`
 *
 * Lists records for an entity with:
 *  - Dynamic column generation from entity field schema
 *  - Field-type-aware cell rendering (all 20+ FieldType variants)
 *  - Server-side pagination (default page size 15) and sorting via URL params
 *  - Filter drawer with per-field filter inputs
 *  - Permission-gated Create / Edit / Delete actions
 *  - Delete confirmation modal
 *  - Sub-nav tabs for entity admin navigation (Details, Fields, Relations, Data, etc.)
 *
 * Source analysis:
 *  - data.cshtml.cs `OnGet()`: loads entity, checks permissions, orders fields
 *    (id first, then alphabetical), builds sort (default id ASC), paginates
 *    with PagerSize=15
 *  - data.cshtml: renders dynamic grid with field-type-aware cells, filter
 *    drawer (width 550px), Create/Edit/Delete buttons gated by permissions
 *  - `GetFieldAccess()`: checks security/roles → Full / ReadOnly / Forbidden
 *
 * @module pages/admin/AdminEntityData
 */

import { useState, useMemo, useCallback, useEffect } from 'react';
import {
  useParams,
  useNavigate,
  Link,
  useSearchParams,
} from 'react-router-dom';

// Internal hook imports
import { useEntity } from '../../hooks/useEntities';
import { useRecords, useDeleteRecord } from '../../hooks/useRecords';

// Component imports
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import { FilterField } from '../../components/data-table/FilterField';
import Modal, { ModalSize } from '../../components/common/Modal';
import Drawer from '../../components/common/Drawer';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';
import Button, { ButtonColor } from '../../components/common/Button';
import { useToast } from '../../components/common/ScreenMessage';

// Type imports
import type {
  Entity,
  AnyField,
  RecordPermissions,
  Field,
  FieldPermissions,
} from '../../types/entity';
import { FieldType } from '../../types/entity';
import type { EntityRecord } from '../../types/record';
import {
  FilterType,
  QuerySortType,
  QueryType,
} from '../../types/filter';
import type { QueryObject, QuerySortObject } from '../../types/filter';

// Auth store import
import { useCurrentUser } from '../../stores/authStore';

// ScreenMessageType for toast notifications
import { ScreenMessageType } from '../../types/common';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default page size matching the monolith's PagerSize = 15. */
const DEFAULT_PAGE_SIZE = 15;

/** Default sort field matching the monolith's fallback "id ASC". */
const DEFAULT_SORT_FIELD = 'id';

/** Default sort direction. */
const DEFAULT_SORT_ORDER: 'asc' | 'desc' = 'asc';

// ---------------------------------------------------------------------------
// Field Access Utility
// ---------------------------------------------------------------------------

/**
 * Access level for a field, mirroring the monolith's GetFieldAccess() method.
 *
 * data.cshtml.cs lines 270-325:
 *  - No security enabled → Full
 *  - Check user roles against canRead/canUpdate arrays
 *  - If any role in canUpdate → Full (can read + modify)
 *  - If any role in canRead → ReadOnly
 *  - Otherwise → Forbidden (field hidden)
 */
const enum FieldAccessLevel {
  /** Full read + write access. */
  Full = 0,
  /** Read-only — visible but not editable. */
  ReadOnly = 1,
  /** Forbidden — field is hidden entirely. */
  Forbidden = 2,
}

/**
 * Determines the access level for a field based on the current user's roles.
 *
 * Mirrors `GetFieldAccess(Field field)` from data.cshtml.cs:
 *  1. If `field.enableSecurity` is false → Full (no restrictions)
 *  2. Intersect user roles with `field.permissions.canUpdate`; if any match → Full
 *  3. Intersect user roles with `field.permissions.canRead`; if any match → ReadOnly
 *  4. Otherwise → Forbidden
 *
 * Admin users always get Full access (they bypass security checks).
 */
function getFieldAccess(
  field: Field,
  userRoles: string[],
  isAdmin: boolean,
): FieldAccessLevel {
  if (isAdmin) {
    return FieldAccessLevel.Full;
  }
  if (!field.enableSecurity) {
    return FieldAccessLevel.Full;
  }
  const perms: FieldPermissions = field.permissions;
  if (perms.canUpdate.some((roleId) => userRoles.includes(roleId))) {
    return FieldAccessLevel.Full;
  }
  if (perms.canRead.some((roleId) => userRoles.includes(roleId))) {
    return FieldAccessLevel.ReadOnly;
  }
  return FieldAccessLevel.Forbidden;
}

// ---------------------------------------------------------------------------
// Permission Helpers
// ---------------------------------------------------------------------------

/**
 * Checks if the current user has a specific record-level permission.
 *
 * RecordPermissions arrays contain role GUIDs. The monolith checks if
 * the current user has at least one role in the permission array.
 * Admins always pass.
 */
function hasPermission(
  permissionRoles: string[],
  userRoles: string[],
  isAdmin: boolean,
): boolean {
  if (isAdmin) return true;
  if (!permissionRoles || permissionRoles.length === 0) return false;
  return permissionRoles.some((roleId) => userRoles.includes(roleId));
}

// ---------------------------------------------------------------------------
// Filter URL Serialisation Helpers
// ---------------------------------------------------------------------------

/**
 * Prefix used for filter query params in the URL.
 * Matches the monolith's filter param pattern: `f_{fieldName}_{operator}`.
 */
const FILTER_PREFIX = 'f_';

/** Serialised filter state from/to URL search params. */
interface ActiveFilter {
  fieldName: string;
  filterType: FilterType;
  value: string;
  value2?: string;
}

/**
 * Parses active filters from URL search params.
 *
 * Expected format: `f_{fieldName}_{filterTypeNumber}={value}`
 * For BETWEEN/NOTBETWEEN: `f_{fieldName}_{filterTypeNumber}={value}&f_{fieldName}_{filterTypeNumber}_2={value2}`
 */
function parseFiltersFromSearchParams(
  searchParams: URLSearchParams,
): ActiveFilter[] {
  const filters: ActiveFilter[] = [];
  const processedKeys = new Set<string>();

  searchParams.forEach((_value, key) => {
    if (!key.startsWith(FILTER_PREFIX) || key.endsWith('_2')) return;
    if (processedKeys.has(key)) return;
    processedKeys.add(key);

    const rest = key.slice(FILTER_PREFIX.length);
    const lastUnderscoreIdx = rest.lastIndexOf('_');
    if (lastUnderscoreIdx === -1) return;

    const fieldName = rest.slice(0, lastUnderscoreIdx);
    const filterTypeStr = rest.slice(lastUnderscoreIdx + 1);
    const filterType = Number(filterTypeStr) as FilterType;

    if (Number.isNaN(filterType)) return;

    const value = searchParams.get(key) ?? '';
    const value2Key = `${key}_2`;
    const value2 = searchParams.get(value2Key) ?? undefined;

    filters.push({ fieldName, filterType, value, value2 });
  });

  return filters;
}

/**
 * Writes active filters to URL search params.
 */
function writeFiltersToSearchParams(
  filters: ActiveFilter[],
  searchParams: URLSearchParams,
): void {
  // Remove existing filter params
  const keysToRemove: string[] = [];
  searchParams.forEach((_value, key) => {
    if (key.startsWith(FILTER_PREFIX)) {
      keysToRemove.push(key);
    }
  });
  keysToRemove.forEach((key) => searchParams.delete(key));

  // Write new filter params
  for (const filter of filters) {
    if (!filter.value && !filter.value2) continue;
    const key = `${FILTER_PREFIX}${filter.fieldName}_${filter.filterType}`;
    if (filter.value) {
      searchParams.set(key, filter.value);
    }
    if (filter.value2) {
      searchParams.set(`${key}_2`, filter.value2);
    }
  }
}

/**
 * Converts active filters into a QueryObject tree for the API.
 *
 * Builds an AND-compound query node from individual field filters,
 * mapping each FilterType to its corresponding QueryType.
 */
function buildQueryFromFilters(filters: ActiveFilter[]): QueryObject | null {
  if (filters.length === 0) return null;

  const filterTypeToQueryType: Record<number, QueryType> = {
    [FilterType.EQ]: QueryType.EQ,
    [FilterType.NOT]: QueryType.NOT,
    [FilterType.LT]: QueryType.LT,
    [FilterType.LTE]: QueryType.LTE,
    [FilterType.GT]: QueryType.GT,
    [FilterType.GTE]: QueryType.GTE,
    [FilterType.REGEX]: QueryType.REGEX,
    [FilterType.FTS]: QueryType.FTS,
    [FilterType.STARTSWITH]: QueryType.STARTSWITH,
    [FilterType.CONTAINS]: QueryType.CONTAINS,
  };

  const subQueries: QueryObject[] = [];

  for (const filter of filters) {
    if (!filter.value && filter.filterType !== FilterType.EQ) continue;

    if (
      filter.filterType === FilterType.BETWEEN ||
      filter.filterType === FilterType.NOTBETWEEN
    ) {
      // BETWEEN/NOTBETWEEN: translate to GTE+LTE / (LT OR GT) compound
      if (filter.value && filter.value2) {
        if (filter.filterType === FilterType.BETWEEN) {
          subQueries.push({
            queryType: QueryType.AND,
            fieldName: '',
            fieldValue: null,
            subQueries: [
              {
                queryType: QueryType.GTE,
                fieldName: filter.fieldName,
                fieldValue: filter.value,
                subQueries: [],
              },
              {
                queryType: QueryType.LTE,
                fieldName: filter.fieldName,
                fieldValue: filter.value2,
                subQueries: [],
              },
            ],
          });
        } else {
          subQueries.push({
            queryType: QueryType.OR,
            fieldName: '',
            fieldValue: null,
            subQueries: [
              {
                queryType: QueryType.LT,
                fieldName: filter.fieldName,
                fieldValue: filter.value,
                subQueries: [],
              },
              {
                queryType: QueryType.GT,
                fieldName: filter.fieldName,
                fieldValue: filter.value2,
                subQueries: [],
              },
            ],
          });
        }
      }
    } else {
      const queryType = filterTypeToQueryType[filter.filterType];
      if (queryType !== undefined) {
        subQueries.push({
          queryType,
          fieldName: filter.fieldName,
          fieldValue: filter.value,
          subQueries: [],
        });
      }
    }
  }

  if (subQueries.length === 0) return null;
  if (subQueries.length === 1) return subQueries[0];

  return {
    queryType: QueryType.AND,
    fieldName: '',
    fieldValue: null,
    subQueries,
  };
}

// ---------------------------------------------------------------------------
// Cell Rendering Helpers
// ---------------------------------------------------------------------------

/**
 * Formats a cell value for display based on the field type.
 *
 * Mirrors the monolith's field-type switch in data.cshtml (lines 148-340),
 * handling all 20+ field types with appropriate formatting.
 */
function renderCellValue(
  value: unknown,
  field: AnyField,
): React.ReactNode {
  if (value === null || value === undefined || value === '') {
    return '';
  }

  switch (field.fieldType) {
    case FieldType.AutoNumberField: {
      const numVal = value as number;
      if ('displayFormat' in field && field.displayFormat) {
        // Basic zero-padded format: "{0:00000}" → pad to 5 digits
        const formatMatch = field.displayFormat.match(/\{0:0+\}/);
        if (formatMatch) {
          const padLength = formatMatch[0].length - 4; // subtract "{0:" and "}"
          return String(numVal).padStart(padLength, '0');
        }
      }
      return String(numVal);
    }

    case FieldType.CheckboxField: {
      const boolVal = value as boolean;
      return (
        <span
          className={`inline-flex items-center justify-center size-5 rounded text-white text-xs ${
            boolVal
              ? 'bg-green-600'
              : 'bg-gray-300 dark:bg-gray-600'
          }`}
          aria-label={boolVal ? 'Yes' : 'No'}
        >
          {boolVal ? '✓' : '✗'}
        </span>
      );
    }

    case FieldType.CurrencyField: {
      const currencyVal = value as number;
      if ('currency' in field && field.currency) {
        const { symbol, decimalDigits } = field.currency;
        return `${symbol}${currencyVal.toFixed(decimalDigits)}`;
      }
      return String(currencyVal);
    }

    case FieldType.DateField: {
      const dateStr = value as string;
      try {
        const date = new Date(dateStr);
        if (Number.isNaN(date.getTime())) return String(value);
        // Use the field's format if available, otherwise default ISO date
        return date.toLocaleDateString(undefined, {
          year: 'numeric',
          month: '2-digit',
          day: '2-digit',
        });
      } catch {
        return String(value);
      }
    }

    case FieldType.DateTimeField: {
      const dtStr = value as string;
      try {
        const dt = new Date(dtStr);
        if (Number.isNaN(dt.getTime())) return String(value);
        return dt.toLocaleString(undefined, {
          year: 'numeric',
          month: '2-digit',
          day: '2-digit',
          hour: '2-digit',
          minute: '2-digit',
        });
      } catch {
        return String(value);
      }
    }

    case FieldType.EmailField: {
      const email = String(value);
      return (
        <a
          href={`mailto:${email}`}
          className="text-blue-600 hover:underline dark:text-blue-400"
        >
          {email}
        </a>
      );
    }

    case FieldType.FileField: {
      const filePath = String(value);
      if (!filePath) return '';
      const fileName = filePath.split('/').pop() ?? filePath;
      return (
        <span className="inline-flex items-center gap-1 text-sm">
          <span className="i-lucide-file text-gray-500" aria-hidden="true" />
          {fileName}
        </span>
      );
    }

    case FieldType.GeographyField: {
      // The monolith renders an interactive Leaflet map for Geography fields.
      // In the data grid we show a text summary; a full map view belongs on the
      // record detail page.
      const geoVal = value;
      if (typeof geoVal === 'string') {
        // WKT or GeoJSON string — truncate for display
        const display =
          geoVal.length > 40 ? `${geoVal.slice(0, 40)}…` : geoVal;
        return (
          <span className="font-mono text-xs text-gray-600 dark:text-gray-400">
            {display}
          </span>
        );
      }
      if (typeof geoVal === 'object' && geoVal !== null) {
        // GeoJSON object — show type
        const geoObj = geoVal as Record<string, unknown>;
        return (
          <span className="font-mono text-xs text-gray-600 dark:text-gray-400">
            {String(geoObj['type'] ?? 'GeoJSON')}
          </span>
        );
      }
      return String(value);
    }

    case FieldType.HtmlField: {
      const htmlStr = String(value);
      // Strip HTML tags for grid display — full HTML renders on detail page
      const stripped = htmlStr.replace(/<[^>]*>/g, '');
      const display =
        stripped.length > 80 ? `${stripped.slice(0, 80)}…` : stripped;
      return display;
    }

    case FieldType.ImageField: {
      const imgPath = String(value);
      if (!imgPath) return '';
      return (
        <img
          src={imgPath}
          alt=""
          className="size-8 rounded object-cover"
          loading="lazy"
          decoding="async"
          width={32}
          height={32}
        />
      );
    }

    case FieldType.MultiLineTextField: {
      const text = String(value);
      const display = text.length > 80 ? `${text.slice(0, 80)}…` : text;
      return display;
    }

    case FieldType.MultiSelectField: {
      const selections = value as string[];
      if (!Array.isArray(selections) || selections.length === 0) return '';
      // Resolve option labels if field has options
      if ('options' in field && Array.isArray(field.options)) {
        const optionMap = new Map(
          field.options.map((o: { value: string; label: string }) => [
            o.value,
            o.label,
          ]),
        );
        return (
          <span className="inline-flex flex-wrap gap-1">
            {selections.map((val) => (
              <span
                key={val}
                className="inline-block rounded bg-gray-200 px-1.5 py-0.5 text-xs dark:bg-gray-700"
              >
                {optionMap.get(val) ?? val}
              </span>
            ))}
          </span>
        );
      }
      return selections.join(', ');
    }

    case FieldType.NumberField: {
      const numericVal = value as number;
      return String(numericVal);
    }

    case FieldType.PasswordField: {
      // Never display actual password values — show masked indicator
      return '••••••';
    }

    case FieldType.PercentField: {
      const pctVal = value as number;
      return `${pctVal}%`;
    }

    case FieldType.PhoneField: {
      const phone = String(value);
      return (
        <a
          href={`tel:${phone}`}
          className="text-blue-600 hover:underline dark:text-blue-400"
        >
          {phone}
        </a>
      );
    }

    case FieldType.GuidField: {
      const guid = String(value);
      // Truncate GUID for display in grid; full value on hover
      return (
        <span
          className="font-mono text-xs text-gray-600 dark:text-gray-400"
          title={guid}
        >
          {guid.length > 13 ? `${guid.slice(0, 13)}…` : guid}
        </span>
      );
    }

    case FieldType.SelectField: {
      const selectVal = String(value);
      // Resolve option label if field has options
      if ('options' in field && Array.isArray(field.options)) {
        const option = field.options.find(
          (o: { value: string; label: string }) => o.value === selectVal,
        );
        if (option) {
          return (
            <span className="inline-flex items-center gap-1">
              {option.color && (
                <span
                  className="inline-block size-2.5 rounded-full"
                  style={{ backgroundColor: option.color }}
                  aria-hidden="true"
                />
              )}
              {option.label}
            </span>
          );
        }
      }
      return selectVal;
    }

    case FieldType.TextField: {
      return String(value);
    }

    case FieldType.UrlField: {
      const url = String(value);
      return (
        <a
          href={url}
          target="_blank"
          rel="noopener noreferrer"
          className="text-blue-600 hover:underline dark:text-blue-400"
        >
          {url.length > 50 ? `${url.slice(0, 50)}…` : url}
        </a>
      );
    }

    /* RelationField (20) — relation fields are not rendered as data columns;
       they are navigation links handled by the monolith's relation components.
       If one shows up, render the raw value. */
    default: {
      return String(value);
    }
  }
}

// ---------------------------------------------------------------------------
// Entity Sub-Navigation Tabs
// ---------------------------------------------------------------------------

/**
 * Builds the sub-navigation tab configuration for an entity admin page.
 *
 * Mirrors the entity_SubNav partial from the SDK plugin which renders
 * tabs for: Details, Fields, Relations, Data, Views, List, etc.
 */
function buildEntitySubNavTabs(
  entityId: string,
  activeTab: string,
): TabConfig[] {
  const tabs: TabConfig[] = [
    {
      id: 'details',
      label: 'Details',
      content: null,
    },
    {
      id: 'fields',
      label: 'Fields',
      content: null,
    },
    {
      id: 'relations',
      label: 'Relations',
      content: null,
    },
    {
      id: 'data',
      label: 'Data',
      content: null,
    },
    {
      id: 'views',
      label: 'Views',
      content: null,
    },
  ];

  // Mark the active tab by assigning non-null content
  return tabs.map((tab) => ({
    ...tab,
    content: tab.id === activeTab ? <span /> : null,
  }));
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * Admin Entity Data page — lists records for an entity with dynamic
 * field rendering, pagination, sorting, filtering, and permission-gated
 * CRUD actions.
 *
 * Route: `/admin/entities/:entityId/data`
 */
export default function AdminEntityData(): React.ReactElement {
  // ── Route params & navigation ───────────────────────────────
  const { entityId } = useParams<{ entityId: string }>();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  // ── Auth / current user ─────────────────────────────────────
  const currentUser = useCurrentUser();
  const userRoles = useMemo(
    () => currentUser?.roles ?? [],
    [currentUser?.roles],
  );
  const isAdmin = currentUser?.isAdmin ?? false;

  // ── Entity data fetching ────────────────────────────────────
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityIsError,
    error: entityError,
  } = useEntity(entityId ?? '');

  // ── Toast notifications ─────────────────────────────────────
  const { showToast } = useToast();

  // ── Local UI state ──────────────────────────────────────────
  const [isFilterDrawerOpen, setIsFilterDrawerOpen] = useState(false);
  const [isDeleteModalOpen, setIsDeleteModalOpen] = useState(false);
  const [recordToDelete, setRecordToDelete] = useState<EntityRecord | null>(
    null,
  );

  // ── URL-derived pagination / sort / filter state ────────────
  const currentPage = useMemo(() => {
    const p = searchParams.get('page');
    const parsed = p ? Number(p) : 1;
    return Number.isNaN(parsed) || parsed < 1 ? 1 : parsed;
  }, [searchParams]);

  const pageSize = useMemo(() => {
    const ps = searchParams.get('pageSize');
    const parsed = ps ? Number(ps) : DEFAULT_PAGE_SIZE;
    return Number.isNaN(parsed) || parsed < 1 ? DEFAULT_PAGE_SIZE : parsed;
  }, [searchParams]);

  const sortBy = useMemo(
    () => searchParams.get('sortBy') ?? DEFAULT_SORT_FIELD,
    [searchParams],
  );

  const sortOrder = useMemo((): 'asc' | 'desc' => {
    const so = searchParams.get('sortOrder');
    return so === 'desc' ? 'desc' : DEFAULT_SORT_ORDER;
  }, [searchParams]);

  // Parse active filters from URL
  const activeFilters = useMemo(
    () => parseFiltersFromSearchParams(searchParams),
    [searchParams],
  );

  // ── Build API query params ──────────────────────────────────
  const queryObject = useMemo(
    () => buildQueryFromFilters(activeFilters),
    [activeFilters],
  );

  const sortObjects = useMemo((): QuerySortObject[] => {
    return [
      {
        fieldName: sortBy,
        sortType:
          sortOrder === 'desc'
            ? QuerySortType.Descending
            : QuerySortType.Ascending,
      },
    ];
  }, [sortBy, sortOrder]);

  const recordsParams = useMemo(
    () => ({
      fields: '*',
      query: queryObject,
      sort: sortObjects,
      skip: (currentPage - 1) * pageSize,
      limit: pageSize,
    }),
    [queryObject, sortObjects, currentPage, pageSize],
  );

  // ── Record data fetching ────────────────────────────────────
  const {
    data: queryResult,
    isLoading: recordsLoading,
    isError: recordsIsError,
    error: recordsError,
  } = useRecords(entity?.name ?? '', recordsParams);

  const records: EntityRecord[] = queryResult?.data ?? [];
  const totalCount: number =
    (queryResult as unknown as { totalCount?: number })?.totalCount ??
    records.length;

  // ── Delete mutation ─────────────────────────────────────────
  const deleteRecordMutation = useDeleteRecord();

  // ── Permissions ─────────────────────────────────────────────
  const canCreate = useMemo(() => {
    if (!entity?.recordPermissions) return false;
    return hasPermission(
      entity.recordPermissions.canCreate,
      userRoles,
      isAdmin,
    );
  }, [entity?.recordPermissions, userRoles, isAdmin]);

  const canUpdate = useMemo(() => {
    if (!entity?.recordPermissions) return false;
    return hasPermission(
      entity.recordPermissions.canUpdate,
      userRoles,
      isAdmin,
    );
  }, [entity?.recordPermissions, userRoles, isAdmin]);

  const canDelete = useMemo(() => {
    if (!entity?.recordPermissions) return false;
    return hasPermission(
      entity.recordPermissions.canDelete,
      userRoles,
      isAdmin,
    );
  }, [entity?.recordPermissions, userRoles, isAdmin]);

  const canRead = useMemo(() => {
    if (!entity?.recordPermissions) return false;
    return hasPermission(
      entity.recordPermissions.canRead,
      userRoles,
      isAdmin,
    );
  }, [entity?.recordPermissions, userRoles, isAdmin]);

  // ── Ordered fields: id first, then alphabetical by name ─────
  const orderedFields = useMemo((): AnyField[] => {
    if (!entity?.fields) return [];

    const allFields = entity.fields as AnyField[];
    const accessible = allFields.filter(
      (f) => getFieldAccess(f, userRoles, isAdmin) !== FieldAccessLevel.Forbidden,
    );

    // Sort: "id" first, then alphabetical by name
    return accessible.sort((a, b) => {
      if (a.name === 'id') return -1;
      if (b.name === 'id') return 1;
      return a.name.localeCompare(b.name);
    });
  }, [entity?.fields, userRoles, isAdmin]);

  // ── Dynamic DataTable columns ───────────────────────────────
  const columns = useMemo((): DataTableColumn<EntityRecord>[] => {
    const fieldColumns: DataTableColumn<EntityRecord>[] = orderedFields.map(
      (field) => {
        const access = getFieldAccess(field, userRoles, isAdmin);
        const isSortable = field.searchable && access !== FieldAccessLevel.Forbidden;

        return {
          id: field.name,
          name: field.name,
          label: field.label || field.name,
          sortable: isSortable,
          searchable: field.searchable,
          noWrap: field.fieldType === FieldType.DateField ||
            field.fieldType === FieldType.DateTimeField ||
            field.fieldType === FieldType.AutoNumberField ||
            field.fieldType === FieldType.GuidField,
          accessorKey: field.name,
          cell: (value: unknown, _record: EntityRecord) =>
            renderCellValue(value, field),
        };
      },
    );

    // Actions column — visible when user can edit or delete
    if (canUpdate || canDelete) {
      fieldColumns.push({
        id: '_actions',
        name: '_actions',
        label: 'Actions',
        sortable: false,
        searchable: false,
        noWrap: true,
        width: '140px',
        horizontalAlign: 'right',
        cell: (_value: unknown, record: EntityRecord) => {
          const recordId = record.id ?? '';
          return (
            <span className="inline-flex items-center gap-1">
              {canUpdate && entity && (
                <Link
                  to={`/admin/entities/${entityId}/records/${recordId}/manage`}
                  className="inline-flex items-center rounded px-2 py-1 text-xs font-medium text-blue-600 hover:bg-blue-50 dark:text-blue-400 dark:hover:bg-blue-900/30"
                >
                  Edit
                </Link>
              )}
              {canDelete && (
                <button
                  type="button"
                  className="inline-flex items-center rounded px-2 py-1 text-xs font-medium text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/30"
                  onClick={() => handleDeleteClick(record)}
                >
                  Delete
                </button>
              )}
            </span>
          );
        },
      });
    }

    return fieldColumns;
  }, [orderedFields, userRoles, isAdmin, canUpdate, canDelete, entity, entityId]);

  // ── Sub-navigation tabs ─────────────────────────────────────
  const subNavTabs = useMemo(
    () => buildEntitySubNavTabs(entityId ?? '', 'data'),
    [entityId],
  );

  // ── Filter drawer state for form inputs ─────────────────────
  const [drawerFilters, setDrawerFilters] = useState<
    Record<string, { filterType: FilterType; value: string; value2?: string }>
  >({});

  // Sync drawer filters with active URL filters when drawer opens
  useEffect(() => {
    if (isFilterDrawerOpen) {
      const filterState: Record<
        string,
        { filterType: FilterType; value: string; value2?: string }
      > = {};
      for (const f of activeFilters) {
        filterState[f.fieldName] = {
          filterType: f.filterType,
          value: f.value,
          value2: f.value2,
        };
      }
      setDrawerFilters(filterState);
    }
  }, [isFilterDrawerOpen, activeFilters]);

  // ── Event Handlers ──────────────────────────────────────────

  /** Opens the delete confirmation modal for a specific record. */
  const handleDeleteClick = useCallback((record: EntityRecord) => {
    setRecordToDelete(record);
    setIsDeleteModalOpen(true);
  }, []);

  /** Confirms deletion and executes the mutation. */
  const handleDeleteConfirm = useCallback(() => {
    if (!recordToDelete?.id || !entity?.name) return;

    deleteRecordMutation.mutate(
      { entityName: entity.name, id: recordToDelete.id },
      {
        onSuccess: () => {
          showToast(
            ScreenMessageType.Success,
            'Record Deleted',
            `Record "${recordToDelete.id}" was successfully deleted.`,
          );
          setIsDeleteModalOpen(false);
          setRecordToDelete(null);
        },
        onError: (err: Error) => {
          showToast(
            ScreenMessageType.Error,
            'Delete Failed',
            err.message || 'An error occurred while deleting the record.',
          );
          setIsDeleteModalOpen(false);
          setRecordToDelete(null);
        },
      },
    );
  }, [recordToDelete, entity?.name, deleteRecordMutation, showToast]);

  /** Cancels deletion and closes the modal. */
  const handleDeleteCancel = useCallback(() => {
    setIsDeleteModalOpen(false);
    setRecordToDelete(null);
  }, []);

  /** Handles page change from the DataTable. */
  const handlePageChange = useCallback(
    (page: number) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('page', String(page));
        return next;
      });
    },
    [setSearchParams],
  );

  /** Handles page size change from the DataTable. */
  const handlePageSizeChange = useCallback(
    (size: number) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('pageSize', String(size));
        next.set('page', '1'); // Reset to first page on size change
        return next;
      });
    },
    [setSearchParams],
  );

  /** Handles sort change from the DataTable. */
  const handleSortChange = useCallback(
    (field: string, order: 'asc' | 'desc') => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('sortBy', field);
        next.set('sortOrder', order);
        next.set('page', '1'); // Reset to first page on sort change
        return next;
      });
    },
    [setSearchParams],
  );

  /** Handles individual filter field change in the drawer. */
  const handleFilterFieldChange = useCallback(
    (
      fieldName: string,
      filterType: FilterType,
      value: string,
      value2?: string,
    ) => {
      setDrawerFilters((prev) => ({
        ...prev,
        [fieldName]: { filterType, value, value2 },
      }));
    },
    [],
  );

  /** Applies the current drawer filters to the URL. */
  const handleApplyFilters = useCallback(() => {
    const newFilters: ActiveFilter[] = [];
    for (const [fieldName, state] of Object.entries(drawerFilters)) {
      if (state.value || state.value2) {
        newFilters.push({
          fieldName,
          filterType: state.filterType,
          value: state.value,
          value2: state.value2,
        });
      }
    }

    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      writeFiltersToSearchParams(newFilters, next);
      next.set('page', '1'); // Reset to first page on filter change
      return next;
    });

    setIsFilterDrawerOpen(false);
  }, [drawerFilters, setSearchParams]);

  /** Clears all active filters. */
  const handleClearFilters = useCallback(() => {
    setDrawerFilters({});
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      writeFiltersToSearchParams([], next);
      next.set('page', '1');
      return next;
    });
    setIsFilterDrawerOpen(false);
  }, [setSearchParams]);

  /** Navigates to create-record page for this entity. */
  const handleCreateRecord = useCallback(() => {
    if (entityId) {
      navigate(`/admin/entities/${entityId}/records/create`);
    }
  }, [entityId, navigate]);

  /** Handles sub-nav tab click by navigating to the corresponding route. */
  const handleTabClick = useCallback(
    (tabId: string) => {
      if (tabId === 'data') return; // Already on data tab
      navigate(`/admin/entities/${entityId}/${tabId}`);
    },
    [entityId, navigate],
  );

  // ── Searchable fields for the filter drawer ─────────────────
  const searchableFields = useMemo((): AnyField[] => {
    return orderedFields.filter((f) => f.searchable);
  }, [orderedFields]);

  // ── Loading state ───────────────────────────────────────────
  if (entityLoading) {
    return (
      <div className="flex min-h-[200px] items-center justify-center">
        <div
          className="size-8 animate-spin rounded-full border-4 border-gray-300 border-t-blue-600"
          role="status"
          aria-label="Loading entity"
        >
          <span className="sr-only">Loading…</span>
        </div>
      </div>
    );
  }

  // ── Error state ─────────────────────────────────────────────
  if (entityIsError || !entity) {
    return (
      <div
        className="rounded-md border border-red-200 bg-red-50 p-4 text-red-700 dark:border-red-800 dark:bg-red-900/30 dark:text-red-400"
        role="alert"
      >
        <h2 className="mb-2 text-lg font-semibold">Error Loading Entity</h2>
        <p>
          {entityError instanceof Error
            ? entityError.message
            : 'Failed to load entity data. Please try again.'}
        </p>
      </div>
    );
  }

  // ── Permission denied state ─────────────────────────────────
  if (!canRead) {
    return (
      <div
        className="rounded-md border border-yellow-200 bg-yellow-50 p-4 text-yellow-700 dark:border-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-400"
        role="alert"
      >
        <h2 className="mb-2 text-lg font-semibold">Access Denied</h2>
        <p>You do not have permission to read records for this entity.</p>
      </div>
    );
  }

  // ── Derive entity colour for header styling ─────────────────
  const entityColor = entity.color || '#1e88e5';

  return (
    <div className="flex flex-col gap-4">
      {/* ── Page Header ───────────────────────────────────────── */}
      <div className="flex flex-col gap-3">
        {/* Entity icon + name + label */}
        <div className="flex items-center gap-3">
          {entity.iconName && (
            <span
              className="inline-flex size-10 items-center justify-center rounded-lg text-xl text-white"
              style={{ backgroundColor: entityColor }}
              aria-hidden="true"
            >
              <i className={entity.iconName} />
            </span>
          )}
          <div className="flex flex-col">
            <h1 className="text-xl font-bold text-gray-900 dark:text-gray-100">
              {entity.label || entity.name}
            </h1>
            <span className="text-sm text-gray-500 dark:text-gray-400">
              {entity.name}
            </span>
          </div>
        </div>

        {/* Sub-navigation tabs */}
        <nav aria-label="Entity admin navigation">
          <div className="flex gap-0 border-b border-gray-200 dark:border-gray-700">
            {subNavTabs.map((tab) => (
              <button
                key={tab.id}
                type="button"
                onClick={() => handleTabClick(tab.id)}
                className={`px-4 py-2 text-sm font-medium transition-colors ${
                  tab.id === 'data'
                    ? 'border-b-2 text-blue-600 dark:text-blue-400'
                    : 'text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200'
                }`}
                style={
                  tab.id === 'data' ? { borderBottomColor: entityColor } : undefined
                }
                aria-current={tab.id === 'data' ? 'page' : undefined}
              >
                {tab.label}
              </button>
            ))}
          </div>
        </nav>
      </div>

      {/* ── Toolbar ───────────────────────────────────────────── */}
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2">
          {/* Filter button */}
          <Button
            color={ButtonColor.Light}
            text="Filters"
            iconClass="fa fa-filter"
            onClick={() => setIsFilterDrawerOpen(true)}
          />
          {/* Active filter count badge */}
          {activeFilters.length > 0 && (
            <span className="inline-flex size-6 items-center justify-center rounded-full bg-blue-600 text-xs font-medium text-white">
              {activeFilters.length}
            </span>
          )}
        </div>

        <div className="flex items-center gap-2">
          {/* Create Record button — permission-gated */}
          {canCreate && (
            <Button
              color={ButtonColor.Success}
              text="Create Record"
              iconClass="fa fa-plus"
              onClick={handleCreateRecord}
            />
          )}
        </div>
      </div>

      {/* ── Records Error State ───────────────────────────────── */}
      {recordsIsError && (
        <div
          className="rounded-md border border-red-200 bg-red-50 p-4 text-red-700 dark:border-red-800 dark:bg-red-900/30 dark:text-red-400"
          role="alert"
        >
          <p>
            {recordsError instanceof Error
              ? recordsError.message
              : 'Failed to load records. Please try again.'}
          </p>
        </div>
      )}

      {/* ── DataTable ─────────────────────────────────────────── */}
      <DataTable<EntityRecord>
        data={records}
        columns={columns}
        totalCount={totalCount}
        pageSize={pageSize}
        currentPage={currentPage}
        onPageChange={handlePageChange}
        onPageSizeChange={handlePageSizeChange}
        onSortChange={handleSortChange}
        bordered
        hover
        small
        loading={recordsLoading}
        emptyText="No records found"
        sortByParam="sortBy"
        sortOrderParam="sortOrder"
        pageParam="page"
        pageSizeParam="pageSize"
      />

      {/* ── Filter Drawer ─────────────────────────────────────── */}
      <Drawer
        isVisible={isFilterDrawerOpen}
        width="550px"
        title="Filters"
        onClose={() => setIsFilterDrawerOpen(false)}
        titleAction={
          <div className="flex items-center gap-2">
            <Button
              color={ButtonColor.Primary}
              text="Apply"
              onClick={handleApplyFilters}
            />
            <Button
              color={ButtonColor.Light}
              text="Clear"
              onClick={handleClearFilters}
            />
          </div>
        }
      >
        <div className="flex flex-col gap-4 p-4">
          {searchableFields.length === 0 && (
            <p className="text-sm text-gray-500 dark:text-gray-400">
              No searchable fields available for this entity.
            </p>
          )}
          {searchableFields.map((field) => {
            const currentFilter = drawerFilters[field.name];
            return (
              <FilterField
                key={field.name}
                name={field.name}
                label={field.label || field.name}
                fieldType={field.fieldType}
                queryType={currentFilter?.filterType ?? FilterType.CONTAINS}
                value={currentFilter?.value ?? ''}
                value2={currentFilter?.value2 ?? ''}
                isVisible
                onChange={(
                  name: string,
                  filterType: FilterType,
                  value: string,
                  value2?: string,
                ) => handleFilterFieldChange(name, filterType, value, value2)}
                valueOptions={
                  'options' in field && Array.isArray(field.options)
                    ? field.options.map(
                        (opt: { value: string; label: string }) => ({
                          value: opt.value,
                          label: opt.label,
                        }),
                      )
                    : undefined
                }
              />
            );
          })}
        </div>
      </Drawer>

      {/* ── Delete Confirmation Modal ─────────────────────────── */}
      <Modal
        isVisible={isDeleteModalOpen}
        title="Confirm Delete"
        size={ModalSize.Normal}
        onClose={handleDeleteCancel}
      >
        <div className="flex flex-col gap-4 p-4">
          <p className="text-gray-700 dark:text-gray-300">
            Are you sure you want to delete this record?
          </p>
          {recordToDelete?.id && (
            <p className="text-sm text-gray-500 dark:text-gray-400">
              <span className="font-medium">Record ID:</span>{' '}
              <code className="rounded bg-gray-100 px-1 py-0.5 font-mono text-xs dark:bg-gray-800">
                {recordToDelete.id}
              </code>
            </p>
          )}
          {entity?.name && (
            <p className="text-sm text-gray-500 dark:text-gray-400">
              <span className="font-medium">Entity:</span> {entity.label || entity.name}
            </p>
          )}
          <div className="flex items-center justify-end gap-2 pt-2">
            <Button
              color={ButtonColor.Light}
              text="Cancel"
              onClick={handleDeleteCancel}
            />
            <Button
              color={ButtonColor.Danger}
              text={deleteRecordMutation.isPending ? 'Deleting…' : 'Delete'}
              isDisabled={deleteRecordMutation.isPending}
              onClick={handleDeleteConfirm}
            />
          </div>
        </div>
      </Modal>
    </div>
  );
}
