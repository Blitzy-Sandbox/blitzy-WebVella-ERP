import React, { useState, useCallback, useMemo } from 'react';
import { useMutation } from '@tanstack/react-query';
import { post, type ApiResponse } from '../../api/client';
import { DataTable, type DataTableColumn } from '../../components/data-table/DataTable';
import TabNav, { type TabConfig } from '../../components/common/TabNav';
import { useEntities, useRelations } from '../../hooks/useEntities';
import {
  EntityRelationType,
  type Entity,
  type EntityRelation,
} from '../../types/entity';

/* ═══════════════════════════════════════════════════════════════════════════
 * Local type definitions — mirror the API response shapes from the monolith's
 * CodeGenService.EvaluateMetaChanges() output (MetaChangeModel).
 * ═══════════════════════════════════════════════════════════════════════════ */

/** Possible change statuses returned by the code-generation comparison engine. */
type ChangeType = 'created' | 'updated' | 'deleted';

/**
 * A single metadata difference detected between the source and target.
 *
 * The index signature satisfies DataTable's `T extends Record<string, unknown>`
 * constraint while preserving strongly-typed property access.
 */
interface MetaChange {
  [key: string]: unknown;
  /** Schema element kind that changed (entity, field, relation, role, etc.). */
  element: string;
  /** Whether the element was created, updated, or deleted. */
  type: ChangeType;
  /** Human-readable name of the changed element. */
  name: string;
  /** Granular list of individual property-level changes within the element. */
  changeList: string[];
}

/** Payload sent to the code-generation API endpoint. */
interface CodeGenRequest {
  connectionString: string;
  includeEntityMeta: boolean;
  includeEntityRelations: boolean;
  includeUserRoles: boolean;
  includeApplications: boolean;
  includeRecordsEntityIdList: string[];
  includeNNRelationIdList: string[];
}

/** Shape of the response payload inside ApiResponse.object. */
interface CodeGenResult {
  changes: MetaChange[];
  code: string;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Presentation helpers
 * ═══════════════════════════════════════════════════════════════════════════ */

/** Tailwind class maps for each change-type badge (green / amber / red). */
const BADGE_STYLES: Record<ChangeType, string> = {
  created:
    'inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium bg-green-50 text-green-700 ring-1 ring-inset ring-green-600/20',
  updated:
    'inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium bg-amber-50 text-amber-700 ring-1 ring-inset ring-amber-600/20',
  deleted:
    'inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium bg-red-50 text-red-700 ring-1 ring-inset ring-red-600/20',
};

/** Returns a colour-coded pill badge for a given change type. */
function renderStatusBadge(changeType: ChangeType): React.ReactNode {
  return (
    <span className={BADGE_STYLES[changeType] ?? ''}>
      {changeType}
    </span>
  );
}

/* ═══════════════════════════════════════════════════════════════════════════
 * CodeGenTool — Admin page component
 *
 * Route: /admin/tools/codegen
 *
 * Replaces the monolith's
 *   WebVella.Erp.Plugins.SDK/Pages/tools/cogegen.cshtml[.cs]
 *
 * The tool compares the current service metadata state against entity
 * definitions from a target database and generates C# patch code to
 * synchronise differences between environments.
 * ═══════════════════════════════════════════════════════════════════════════ */

function CodeGenTool(): React.JSX.Element {
  /* ── Form state ──────────────────────────────────────────────────────── */
  const [connectionString, setConnectionString] = useState('');
  const [includeEntityMeta, setIncludeEntityMeta] = useState(true);
  const [includeEntityRelations, setIncludeEntityRelations] = useState(true);
  const [includeUserRoles, setIncludeUserRoles] = useState(true);
  const [includeApplications, setIncludeApplications] = useState(true);
  const [selectedEntityIds, setSelectedEntityIds] = useState<string[]>([]);
  const [selectedRelationIds, setSelectedRelationIds] = useState<string[]>([]);
  const [validationError, setValidationError] = useState('');
  const [activeTab, setActiveTab] = useState('changes');

  /* ── Remote data (entity & relation lists for the multiselects) ────── */
  const { data: entities = [], isLoading: entitiesLoading } = useEntities();
  const { data: relations = [], isLoading: relationsLoading } = useRelations();

  /** Entities sorted alphabetically — mirrors the monolith's OrderBy(x => x.Name). */
  const sortedEntities = useMemo<Entity[]>(
    () => [...entities].sort((a, b) => a.name.localeCompare(b.name)),
    [entities],
  );

  /**
   * Only ManyToMany relations are shown for N:N record inclusion — mirrors
   * the monolith's .Where(x => x.RelationType == ManyToMany).OrderBy(x => x.Name).
   */
  const manyToManyRelations = useMemo<EntityRelation[]>(
    () =>
      relations
        .filter((r) => r.relationType === EntityRelationType.ManyToMany)
        .sort((a, b) => a.name.localeCompare(b.name)),
    [relations],
  );

  /* ── Code-generation mutation ────────────────────────────────────────── */
  const {
    mutate,
    isPending,
    isError: isMutationError,
    error: mutationError,
    data: mutationResult,
  } = useMutation<ApiResponse<CodeGenResult>, Error, CodeGenRequest>({
    mutationFn: (request: CodeGenRequest) =>
      post<CodeGenResult>('/v1/admin/codegen/evaluate', request),
  });

  /* ── Derived result state ────────────────────────────────────────────── */
  const showResults = mutationResult?.success === true;
  const changes: MetaChange[] = mutationResult?.object?.changes ?? [];
  const code: string = mutationResult?.object?.code ?? '';

  /* ── DataTable column definitions (Changes tab) ──────────────────────── */
  const columns = useMemo<DataTableColumn<MetaChange>[]>(
    () => [
      {
        id: 'element',
        label: 'Element',
        accessorKey: 'element',
        width: '120px',
      },
      {
        id: 'change',
        label: 'Change',
        accessorFn: (record: MetaChange) => record.type,
        cell: (_value: unknown, record: MetaChange) =>
          renderStatusBadge(record.type),
        width: '110px',
      },
      {
        id: 'name',
        label: 'Name',
        accessorKey: 'name',
        width: '200px',
      },
      {
        id: 'description',
        label: 'Description',
        cell: (_value: unknown, record: MetaChange) => {
          const { changeList } = record;
          if (!changeList || changeList.length === 0) {
            return null;
          }
          return (
            <ul className="list-disc list-inside space-y-0.5 text-sm text-gray-600">
              {changeList.map((detail, idx) => (
                <li key={idx}>{detail}</li>
              ))}
            </ul>
          );
        },
      },
    ],
    [],
  );

  /* ── Tab configuration for the results section ───────────────────────── */
  const resultTabs = useMemo<TabConfig[]>(
    () => [
      {
        id: 'changes',
        label: 'Changes',
        content: (
          <div className="mt-4">
            {changes.length === 0 ? (
              <p className="text-sm text-gray-500 italic py-4 text-center">
                No metadata differences detected.
              </p>
            ) : (
              <DataTable<MetaChange> data={changes} columns={columns} />
            )}
          </div>
        ),
      },
      {
        id: 'code',
        label: 'Code',
        content: (
          <div className="mt-4">
            {code ? (
              <pre
                className="bg-gray-900 text-gray-100 rounded-lg p-4 overflow-auto max-h-[600px] text-sm leading-relaxed"
                aria-label="Generated C# migration code"
              >
                <code className="font-mono whitespace-pre">{code}</code>
              </pre>
            ) : (
              <p className="text-sm text-gray-500 italic py-4 text-center">
                No code generated.
              </p>
            )}
          </div>
        ),
      },
    ],
    [changes, columns, code],
  );

  /* ── Event handlers ──────────────────────────────────────────────────── */
  const handleTabChange = useCallback((tabId: string) => {
    setActiveTab(tabId);
  }, []);

  const handleEntitySelectChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      const selected = Array.from(e.target.selectedOptions, (opt) => opt.value);
      setSelectedEntityIds(selected);
    },
    [],
  );

  const handleRelationSelectChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      const selected = Array.from(e.target.selectedOptions, (opt) => opt.value);
      setSelectedRelationIds(selected);
    },
    [],
  );

  const handleSubmit = useCallback(
    (e: React.FormEvent<HTMLFormElement>) => {
      e.preventDefault();
      setValidationError('');

      const trimmed = connectionString.trim();
      if (!trimmed) {
        setValidationError('Connection string is required.');
        return;
      }

      mutate({
        connectionString: trimmed,
        includeEntityMeta,
        includeEntityRelations,
        includeUserRoles,
        includeApplications,
        includeRecordsEntityIdList: selectedEntityIds,
        includeNNRelationIdList: selectedRelationIds,
      });
    },
    [
      connectionString,
      includeEntityMeta,
      includeEntityRelations,
      includeUserRoles,
      includeApplications,
      selectedEntityIds,
      selectedRelationIds,
      mutate,
    ],
  );

  /* ── Render ──────────────────────────────────────────────────────────── */
  return (
    <main className="mx-auto max-w-5xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ── Page header ─────────────────────────────────────────────── */}
      <header className="mb-6 flex items-center gap-3">
        <div
          className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-red-100 text-red-600"
          aria-hidden="true"
        >
          {/* Cloud-download icon matching the monolith's fa-cloud-download-alt */}
          <svg
            className="h-5 w-5"
            fill="none"
            viewBox="0 0 24 24"
            strokeWidth={1.5}
            stroke="currentColor"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M12 16.5V9.75m0 0 3 3m-3-3-3 3M6.75 19.5a4.5 4.5 0 0 1-1.41-8.775 5.25 5.25 0 0 1 10.233-2.33 3 3 0 0 1 3.758 3.848A3.752 3.752 0 0 1 18 19.5H6.75Z"
            />
          </svg>
        </div>
        <h1 className="text-2xl font-bold text-gray-900">
          Code Generation / Metadata Comparison
        </h1>
      </header>

      {/* ── Info alert ───────────────────────────────────────────────── */}
      <div
        className="mb-6 rounded-lg border border-blue-200 bg-blue-50 p-4 text-sm text-blue-800"
        role="alert"
      >
        <strong className="font-semibold">Important:</strong>{' '}
        This tool compares the current service metadata state with entity
        definitions from a target database and generates C# patch code to
        synchronise differences. It is used to migrate metadata changes
        between environments.
      </div>

      {/* ── Validation / mutation errors ─────────────────────────────── */}
      {validationError && (
        <div
          className="mb-6 rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-800"
          role="alert"
        >
          {validationError}
        </div>
      )}

      {isMutationError && (
        <div
          className="mb-6 rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-800"
          role="alert"
        >
          {mutationError instanceof Error
            ? mutationError.message
            : 'An unexpected error occurred during code generation.'}
        </div>
      )}

      {mutationResult && !mutationResult.success && mutationResult.errors && (
        <div
          className="mb-6 rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-800"
          role="alert"
        >
          {mutationResult.errors.map((err, idx) => (
            <div key={idx}>{err.message || err.value || 'Unknown error'}</div>
          ))}
        </div>
      )}

      {/* ── Configuration form ───────────────────────────────────────── */}
      <form onSubmit={handleSubmit} noValidate>
        {/* Connection string */}
        <div className="mb-5">
          <label
            htmlFor="codegen-connection-string"
            className="mb-1.5 block text-sm font-medium text-gray-700"
          >
            Connection String
          </label>
          <input
            id="codegen-connection-string"
            type="text"
            className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus-visible:border-blue-500 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
            placeholder="Host=localhost;Database=mydb;Username=user;Password=pass"
            value={connectionString}
            onChange={(e) => setConnectionString(e.target.value)}
            required
            aria-describedby="codegen-cs-help"
          />
          <p id="codegen-cs-help" className="mt-1 text-xs text-gray-500">
            Full Npgsql connection string or bare database name.
          </p>
        </div>

        {/* Inclusion checkboxes */}
        <fieldset className="mb-5">
          <legend className="mb-2 text-sm font-medium text-gray-700">
            Include in comparison
          </legend>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <label className="flex items-center gap-2 text-sm text-gray-700 cursor-pointer">
              <input
                type="checkbox"
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:ring-blue-500"
                checked={includeEntityMeta}
                onChange={(e) => setIncludeEntityMeta(e.target.checked)}
              />
              Entity Meta
            </label>
            <label className="flex items-center gap-2 text-sm text-gray-700 cursor-pointer">
              <input
                type="checkbox"
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:ring-blue-500"
                checked={includeEntityRelations}
                onChange={(e) => setIncludeEntityRelations(e.target.checked)}
              />
              Entity Relations
            </label>
            <label className="flex items-center gap-2 text-sm text-gray-700 cursor-pointer">
              <input
                type="checkbox"
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:ring-blue-500"
                checked={includeUserRoles}
                onChange={(e) => setIncludeUserRoles(e.target.checked)}
              />
              User Roles
            </label>
            <label className="flex items-center gap-2 text-sm text-gray-700 cursor-pointer">
              <input
                type="checkbox"
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:ring-blue-500"
                checked={includeApplications}
                onChange={(e) => setIncludeApplications(e.target.checked)}
              />
              Applications
            </label>
          </div>
        </fieldset>

        {/* Entity & Relation multiselects */}
        <div className="mb-5 grid grid-cols-1 gap-5 sm:grid-cols-2">
          {/* Entity records inclusion */}
          <div>
            <label
              htmlFor="codegen-entity-select"
              className="mb-1.5 block text-sm font-medium text-gray-700"
            >
              Include Records for Entities
            </label>
            {entitiesLoading ? (
              <div className="flex h-[200px] items-center justify-center rounded-md border border-gray-200 bg-gray-50 text-sm text-gray-500">
                Loading entities…
              </div>
            ) : (
              <select
                id="codegen-entity-select"
                multiple
                className="block h-[200px] w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm shadow-sm focus-visible:border-blue-500 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
                value={selectedEntityIds}
                onChange={handleEntitySelectChange}
                aria-describedby="codegen-entity-help"
              >
                {sortedEntities.map((entity) => (
                  <option key={entity.id} value={entity.id}>
                    {entity.name}
                  </option>
                ))}
              </select>
            )}
            <p id="codegen-entity-help" className="mt-1 text-xs text-gray-500">
              Hold Ctrl/Cmd to select multiple entities.
            </p>
          </div>

          {/* N:N relation records inclusion */}
          <div>
            <label
              htmlFor="codegen-relation-select"
              className="mb-1.5 block text-sm font-medium text-gray-700"
            >
              Include N:N Relation Records
            </label>
            {relationsLoading ? (
              <div className="flex h-[200px] items-center justify-center rounded-md border border-gray-200 bg-gray-50 text-sm text-gray-500">
                Loading relations…
              </div>
            ) : (
              <select
                id="codegen-relation-select"
                multiple
                className="block h-[200px] w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm shadow-sm focus-visible:border-blue-500 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
                value={selectedRelationIds}
                onChange={handleRelationSelectChange}
                aria-describedby="codegen-relation-help"
              >
                {manyToManyRelations.map((relation) => (
                  <option key={relation.id} value={relation.id}>
                    {relation.name}
                  </option>
                ))}
              </select>
            )}
            <p
              id="codegen-relation-help"
              className="mt-1 text-xs text-gray-500"
            >
              Only Many-to-Many relations are shown. Hold Ctrl/Cmd to
              select multiple.
            </p>
          </div>
        </div>

        {/* Submit button */}
        <div className="mt-6">
          <button
            type="submit"
            disabled={isPending}
            className="inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-semibold text-white shadow-sm transition-colors duration-150 hover:bg-red-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-600 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isPending ? (
              <>
                <svg
                  className="h-4 w-4 animate-spin"
                  fill="none"
                  viewBox="0 0 24 24"
                  aria-hidden="true"
                >
                  <circle
                    className="opacity-25"
                    cx="12"
                    cy="12"
                    r="10"
                    stroke="currentColor"
                    strokeWidth="4"
                  />
                  <path
                    className="opacity-75"
                    fill="currentColor"
                    d="M4 12a8 8 0 0 1 8-8V0C5.373 0 0 5.373 0 12h4z"
                  />
                </svg>
                Generating…
              </>
            ) : (
              'Generate'
            )}
          </button>
        </div>
      </form>

      {/* ── Results section ──────────────────────────────────────────── */}
      {showResults && (
        <section className="mt-8" aria-label="Code generation results">
          <div className="rounded-lg border border-gray-200 bg-white shadow-sm">
            <div className="p-4">
              <TabNav
                tabs={resultTabs}
                activeTabId={activeTab}
                onTabChange={handleTabChange}
              />
            </div>
          </div>
        </section>
      )}
    </main>
  );
}

export default CodeGenTool;
