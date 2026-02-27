/**
 * ApplicationSitemap — Sitemap Tree Editor Page
 *
 * React page component replacing the monolith's SDK plugin page:
 *   - `WebVella.Erp.Plugins.SDK/Pages/application/sitemap.cshtml`
 *   - `WebVella.Erp.Plugins.SDK/Pages/application/sitemap.cshtml.cs`
 *
 * Route: `/admin/applications/:appId/sitemap`
 *
 * Interactive sitemap tree editor replacing the Stencil `<wv-sitemap-manager>`
 * web component. Provides hierarchical tree display (App → Areas → Nodes),
 * drag-and-drop reordering, inline property editing for areas and nodes,
 * CRUD operations (add/edit/delete), and entity binding for entity-list nodes.
 *
 * Sub-navigation tabs (Details, Manage, Pages, Sitemap) replicate the
 * monolith's `AdminPageUtils.GetAppAdminSubNav(App, "sitemap")` toolbar.
 *
 * @module pages/admin/ApplicationSitemap
 */

import { useState, useCallback, useMemo, useEffect } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import {
  useApp,
  useCreateArea,
  useUpdateArea,
  useDeleteArea,
  useCreateAreaNode,
  useUpdateAreaNode,
  useDeleteAreaNode,
  useOrderSitemap,
} from '../../hooks/useApps';
import { useEntities } from '../../hooks/useEntities';
import type {
  App,
  SitemapArea,
  SitemapNode,
  SitemapGroup,
  SitemapNodeType,
  Sitemap,
} from '../../types/app';
import Modal from '../../components/common/Modal';
import TabNav, { type TabConfig } from '../../components/common/TabNav';

// ---------------------------------------------------------------------------
// Local constants — SitemapNodeType numeric values
// ---------------------------------------------------------------------------
// The source `const enum SitemapNodeType` cannot be imported as values with
// `isolatedModules: true` (esbuild/Vite transpiles files individually).
// These constants mirror the enum members for runtime comparison.

/** Node type: linked to an entity record list view. */
const NODE_TYPE_ENTITY_LIST = 1;
/** Node type: standalone application page. */
const NODE_TYPE_APPLICATION_PAGE = 2;
/** Node type: external or hard-coded URL. */
const NODE_TYPE_URL = 3;

/** Options for the node-type dropdown in the property editor. */
const NODE_TYPE_OPTIONS: ReadonlyArray<{ value: number; label: string }> = [
  { value: NODE_TYPE_ENTITY_LIST, label: 'Entity List' },
  { value: NODE_TYPE_APPLICATION_PAGE, label: 'Application Page' },
  { value: NODE_TYPE_URL, label: 'URL' },
];

// ---------------------------------------------------------------------------
// Local types
// ---------------------------------------------------------------------------

/** Form state for creating or editing a sitemap area. */
interface AreaFormState {
  id: string;
  name: string;
  label: string;
  description: string;
  iconClass: string;
  weight: number;
  color: string;
  showGroupNames: boolean;
}

/** Form state for creating or editing a sitemap node. */
interface NodeFormState {
  id: string;
  areaId: string;
  name: string;
  label: string;
  iconClass: string;
  weight: number;
  url: string;
  type: number;
  entityId: string;
  groupName: string;
}

/** Information about an item pending delete confirmation. */
interface DeleteTarget {
  type: 'area' | 'node';
  id: string;
  areaId?: string;
  label: string;
}

/** Transient state during a drag-and-drop operation. */
interface DragItem {
  type: 'area' | 'node';
  id: string;
  areaId?: string;
}

/** Blank area form defaults for new-area creation. */
const EMPTY_AREA_FORM: AreaFormState = {
  id: '',
  name: '',
  label: '',
  description: '',
  iconClass: 'fa fa-folder',
  weight: 10,
  color: '#999999',
  showGroupNames: false,
};

/** Blank node form defaults for new-node creation. */
const EMPTY_NODE_FORM: NodeFormState = {
  id: '',
  areaId: '',
  name: '',
  label: '',
  iconClass: 'fa fa-file',
  weight: 10,
  url: '',
  type: NODE_TYPE_ENTITY_LIST,
  entityId: '',
  groupName: '',
};

// ---------------------------------------------------------------------------
// Inline SVG icon helpers (fill="currentColor", no hardcoded size per UI7)
// ---------------------------------------------------------------------------

function PlusIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 448 512"
      fill="currentColor"
      className="h-3.5 w-3.5"
      aria-hidden="true"
    >
      <path d="M256 80c0-17.7-14.3-32-32-32s-32 14.3-32 32v144H48c-17.7 0-32 14.3-32 32s14.3 32 32 32h144v144c0 17.7 14.3 32 32 32s32-14.3 32-32V288h144c17.7 0 32-14.3 32-32s-14.3-32-32-32H256V80z" />
    </svg>
  );
}

function TrashIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 448 512"
      fill="currentColor"
      className="h-3.5 w-3.5"
      aria-hidden="true"
    >
      <path d="M135.2 17.7C140.6 6.8 151.7 0 163.8 0h120.4c12.1 0 23.2 6.8 28.6 17.7L320 32h80c17.7 0 32 14.3 32 32s-14.3 32-32 32H48c-17.7 0-32-14.3-32-32s14.3-32 32-32h80l7.2-14.3zM32 128h384v320c0 35.3-28.7 64-64 64H96c-35.3 0-64-28.7-64-64V128zm96 64c-8.8 0-16 7.2-16 16v224c0 8.8 7.2 16 16 16s16-7.2 16-16V208c0-8.8-7.2-16-16-16zm96 0c-8.8 0-16 7.2-16 16v224c0 8.8 7.2 16 16 16s16-7.2 16-16V208c0-8.8-7.2-16-16-16zm96 0c-8.8 0-16 7.2-16 16v224c0 8.8 7.2 16 16 16s16-7.2 16-16V208c0-8.8-7.2-16-16-16z" />
    </svg>
  );
}

function ChevronDownIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 512 512"
      fill="currentColor"
      className="h-3 w-3"
      aria-hidden="true"
    >
      <path d="M233.4 406.6c12.5 12.5 32.8 12.5 45.3 0l192-192c12.5-12.5 12.5-32.8 0-45.3s-32.8-12.5-45.3 0L256 338.7 86.6 169.4c-12.5-12.5-32.8-12.5-45.3 0s-12.5 32.8 0 45.3l192 192z" />
    </svg>
  );
}

function ChevronRightIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 320 512"
      fill="currentColor"
      className="h-3 w-3"
      aria-hidden="true"
    >
      <path d="M278.6 233.4c12.5 12.5 12.5 32.8 0 45.3l-160 160c-12.5 12.5-32.8 12.5-45.3 0s-12.5-32.8 0-45.3L210.7 256 73.4 118.6c-12.5-12.5-12.5-32.8 0-45.3s32.8-12.5 45.3 0l160 160z" />
    </svg>
  );
}

function GripIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 256 512"
      fill="currentColor"
      className="h-4 w-4 text-gray-400"
      aria-hidden="true"
    >
      <path d="M48 144a48 48 0 1 0 0-96 48 48 0 1 0 0 96zm0 160a48 48 0 1 0 0-96 48 48 0 1 0 0 96zm0 160a48 48 0 1 0 0-96 48 48 0 1 0 0 96zM208 96a48 48 0 1 0 0-96 48 48 0 1 0 0 96zm0 160a48 48 0 1 0 0-96 48 48 0 1 0 0 96zm0 160a48 48 0 1 0 0-96 48 48 0 1 0 0 96z" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Shared Tailwind class tokens
// ---------------------------------------------------------------------------

const INPUT_CLASSES = [
  'block w-full rounded border border-gray-300 bg-white',
  'px-2.5 py-1.5 text-sm text-gray-900',
  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
  'disabled:bg-gray-100 disabled:text-gray-500',
].join(' ');

const LABEL_CLASSES = 'block text-xs font-medium text-gray-600 mb-1';

const BTN_PRIMARY = [
  'inline-flex items-center gap-1.5 rounded bg-blue-600',
  'px-3 py-1.5 text-sm font-medium text-white shadow-sm',
  'hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed',
  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
].join(' ');

const BTN_SECONDARY = [
  'inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white',
  'px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm',
  'hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed',
  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
].join(' ');

const BTN_DANGER = [
  'inline-flex items-center gap-1.5 rounded bg-red-600',
  'px-3 py-1.5 text-sm font-medium text-white shadow-sm',
  'hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed',
  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
].join(' ');

const BTN_ICON = [
  'inline-flex items-center justify-center rounded p-1',
  'text-gray-500 hover:text-gray-700 hover:bg-gray-100',
  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
].join(' ');

// ---------------------------------------------------------------------------
// Helper — returns a human-readable label for a node type value
// ---------------------------------------------------------------------------

function nodeTypeLabel(type: number): string {
  const found = NODE_TYPE_OPTIONS.find((o) => o.value === type);
  return found?.label ?? 'Unknown';
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * **ApplicationSitemap** — Interactive sitemap tree editor for an application.
 *
 * Replaces the monolith's `<wv-sitemap-manager>` Stencil web component and
 * the server-side `PagesModel` from `sitemap.cshtml.cs`.
 *
 * Rendering overview:
 *   1. Page header (breadcrumb, title, subtitle, icon)
 *   2. Sub-nav tabs (Details, Manage, Pages, Sitemap)
 *   3. Two-column layout: tree panel (left) + property editor (right)
 *   4. Delete confirmation modal
 *
 * Default export enables lazy loading via `React.lazy()` for route-level
 * code splitting.
 */
export default function ApplicationSitemap(): React.ReactNode {
  // ── Route parameters ────────────────────────────────────────────────
  const { appId } = useParams<{ appId: string }>();
  const navigate = useNavigate();

  // ── Data hooks ──────────────────────────────────────────────────────
  const { data, isLoading, isError, error } = useApp(appId);
  const { data: entities } = useEntities();
  const createAreaMut = useCreateArea();
  const updateAreaMut = useUpdateArea();
  const deleteAreaMut = useDeleteArea();
  const createNodeMut = useCreateAreaNode();
  const updateNodeMut = useUpdateAreaNode();
  const deleteNodeMut = useDeleteAreaNode();
  const orderSitemapMut = useOrderSitemap();

  const app: App | undefined = data?.object;

  // ── Local state ─────────────────────────────────────────────────────
  const [expandedAreas, setExpandedAreas] = useState<Set<string>>(new Set());
  const [editMode, setEditMode] = useState<'area' | 'node' | null>(null);
  const [isCreating, setIsCreating] = useState(false);
  const [areaForm, setAreaForm] = useState<AreaFormState>(EMPTY_AREA_FORM);
  const [nodeForm, setNodeForm] = useState<NodeFormState>(EMPTY_NODE_FORM);
  const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null);
  const [dragItem, setDragItem] = useState<DragItem | null>(null);
  const [dragOverId, setDragOverId] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);

  // ── Derived data ────────────────────────────────────────────────────
  // Sort areas by weight then name (mirrors monolith's OrderSitemap).
  const sortedAreas = useMemo<SitemapArea[]>(() => {
    if (!app?.sitemap?.areas) return [];
    return [...app.sitemap.areas].sort(
      (a, b) => a.weight - b.weight || a.name.localeCompare(b.name),
    );
  }, [app?.sitemap?.areas]);

  // ── Tab configuration ───────────────────────────────────────────────
  const tabs = useMemo<TabConfig[]>(
    () => [
      { id: 'details', label: 'Details' },
      { id: 'manage', label: 'Manage' },
      { id: 'pages', label: 'Pages' },
      { id: 'sitemap', label: 'Sitemap' },
    ],
    [],
  );

  const handleTabChange = useCallback(
    (tabId: string): void => {
      if (tabId !== 'sitemap' && appId) {
        navigate(`/admin/applications/${appId}/${tabId}`);
      }
    },
    [appId, navigate],
  );

  // ── Expand / collapse ──────────────────────────────────────────────
  const toggleExpand = useCallback((areaId: string) => {
    setExpandedAreas((prev) => {
      const next = new Set(prev);
      if (next.has(areaId)) next.delete(areaId);
      else next.add(areaId);
      return next;
    });
  }, []);

  // ── Selection handlers ─────────────────────────────────────────────
  const selectArea = useCallback((area: SitemapArea) => {
    setEditMode('area');
    setIsCreating(false);
    setSaveError(null);
    setAreaForm({
      id: area.id,
      name: area.name,
      label: area.label,
      description: area.description,
      iconClass: area.iconClass,
      weight: area.weight,
      color: area.color,
      showGroupNames: area.showGroupNames,
    });
  }, []);

  const selectNode = useCallback((node: SitemapNode, areaId: string) => {
    setEditMode('node');
    setIsCreating(false);
    setSaveError(null);
    setNodeForm({
      id: node.id,
      areaId,
      name: node.name,
      label: node.label,
      iconClass: node.iconClass,
      weight: node.weight,
      url: node.url,
      type: node.type as number,
      entityId: node.entityId ?? '',
      groupName: node.groupName,
    });
  }, []);

  // ── Create handlers ────────────────────────────────────────────────
  const startCreateArea = useCallback(() => {
    setEditMode('area');
    setIsCreating(true);
    setSaveError(null);
    setAreaForm({
      ...EMPTY_AREA_FORM,
      weight: (sortedAreas.length + 1) * 10,
    });
  }, [sortedAreas.length]);

  const startCreateNode = useCallback(
    (areaId: string) => {
      const area = sortedAreas.find((a) => a.id === areaId);
      setEditMode('node');
      setIsCreating(true);
      setSaveError(null);
      setNodeForm({
        ...EMPTY_NODE_FORM,
        areaId,
        weight: ((area?.nodes?.length ?? 0) + 1) * 10,
      });
      setExpandedAreas((prev) => new Set(prev).add(areaId));
    },
    [sortedAreas],
  );

  // ── Cancel editing ─────────────────────────────────────────────────
  const handleCancel = useCallback(() => {
    setEditMode(null);
    setIsCreating(false);
    setAreaForm(EMPTY_AREA_FORM);
    setNodeForm(EMPTY_NODE_FORM);
    setSaveError(null);
  }, []);

  // ── Save handler ───────────────────────────────────────────────────
  const isSaving =
    createAreaMut.isPending ||
    updateAreaMut.isPending ||
    createNodeMut.isPending ||
    updateNodeMut.isPending;

  const handleSave = useCallback(async () => {
    if (!appId) return;
    setSaveError(null);

    try {
      if (editMode === 'area' && isCreating) {
        await createAreaMut.mutateAsync({
          appId,
          area: {
            name: areaForm.name,
            label: areaForm.label,
            description: areaForm.description,
            iconClass: areaForm.iconClass,
            weight: areaForm.weight,
            color: areaForm.color,
            showGroupNames: areaForm.showGroupNames,
          },
        });
        handleCancel();
      } else if (editMode === 'area' && !isCreating) {
        await updateAreaMut.mutateAsync({
          appId,
          areaId: areaForm.id,
          area: {
            id: areaForm.id,
            name: areaForm.name,
            label: areaForm.label,
            description: areaForm.description,
            iconClass: areaForm.iconClass,
            weight: areaForm.weight,
            color: areaForm.color,
            showGroupNames: areaForm.showGroupNames,
          },
        });
      } else if (editMode === 'node' && isCreating) {
        await createNodeMut.mutateAsync({
          appId,
          areaId: nodeForm.areaId,
          node: {
            name: nodeForm.name,
            label: nodeForm.label,
            iconClass: nodeForm.iconClass,
            weight: nodeForm.weight,
            url: nodeForm.url,
            type: nodeForm.type as unknown as SitemapNodeType,
            entityId: nodeForm.entityId || null,
            groupName: nodeForm.groupName,
          },
        });
        handleCancel();
      } else if (editMode === 'node' && !isCreating) {
        await updateNodeMut.mutateAsync({
          appId,
          areaId: nodeForm.areaId,
          nodeId: nodeForm.id,
          node: {
            id: nodeForm.id,
            name: nodeForm.name,
            label: nodeForm.label,
            iconClass: nodeForm.iconClass,
            weight: nodeForm.weight,
            url: nodeForm.url,
            type: nodeForm.type as unknown as SitemapNodeType,
            entityId: nodeForm.entityId || null,
            groupName: nodeForm.groupName,
          },
        });
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'An unexpected error occurred while saving.';
      setSaveError(msg);
    }
  }, [
    appId,
    editMode,
    isCreating,
    areaForm,
    nodeForm,
    createAreaMut,
    updateAreaMut,
    createNodeMut,
    updateNodeMut,
    handleCancel,
  ]);

  // ── Delete handlers ────────────────────────────────────────────────
  const isDeleting = deleteAreaMut.isPending || deleteNodeMut.isPending;

  const confirmDelete = useCallback(async () => {
    if (!appId || !deleteTarget) return;
    try {
      if (deleteTarget.type === 'area') {
        await deleteAreaMut.mutateAsync({
          appId,
          areaId: deleteTarget.id,
        });
      } else if (deleteTarget.areaId) {
        await deleteNodeMut.mutateAsync({
          appId,
          areaId: deleteTarget.areaId,
          nodeId: deleteTarget.id,
        });
      }
      // Clear editor if the deleted item was being edited
      if (
        (editMode === 'area' && areaForm.id === deleteTarget.id) ||
        (editMode === 'node' && nodeForm.id === deleteTarget.id)
      ) {
        handleCancel();
      }
      setDeleteTarget(null);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Delete failed.';
      setSaveError(msg);
      setDeleteTarget(null);
    }
  }, [
    appId,
    deleteTarget,
    deleteAreaMut,
    deleteNodeMut,
    editMode,
    areaForm.id,
    nodeForm.id,
    handleCancel,
  ]);

  // ── Drag-and-drop handlers ─────────────────────────────────────────
  const handleDragStart = useCallback(
    (e: React.DragEvent, type: 'area' | 'node', id: string, areaId?: string) => {
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData('text/plain', id);
      setDragItem({ type, id, areaId });
    },
    [],
  );

  const handleDragOver = useCallback(
    (e: React.DragEvent, targetId: string) => {
      e.preventDefault();
      e.dataTransfer.dropEffect = 'move';
      setDragOverId(targetId);
    },
    [],
  );

  const handleDragLeave = useCallback(() => {
    setDragOverId(null);
  }, []);

  const handleDragEnd = useCallback(() => {
    setDragItem(null);
    setDragOverId(null);
  }, []);

  const handleDropArea = useCallback(
    (e: React.DragEvent, targetIndex: number) => {
      e.preventDefault();
      if (!app?.sitemap?.areas || !dragItem || dragItem.type !== 'area' || !appId) {
        setDragItem(null);
        setDragOverId(null);
        return;
      }
      const areas = [...app.sitemap.areas];
      const sourceIndex = areas.findIndex((a) => a.id === dragItem.id);
      if (sourceIndex === -1 || sourceIndex === targetIndex) {
        setDragItem(null);
        setDragOverId(null);
        return;
      }
      const [moved] = areas.splice(sourceIndex, 1);
      areas.splice(targetIndex > sourceIndex ? targetIndex - 1 : targetIndex, 0, moved);
      const reweighted: SitemapArea[] = areas.map((a, i) => ({
        ...a,
        weight: (i + 1) * 10,
      }));
      orderSitemapMut.mutate({ appId, sitemap: { areas: reweighted } });
      setDragItem(null);
      setDragOverId(null);
    },
    [app, dragItem, appId, orderSitemapMut],
  );

  const handleDropNode = useCallback(
    (e: React.DragEvent, targetAreaId: string, targetIndex: number) => {
      e.preventDefault();
      if (!app?.sitemap?.areas || !dragItem || dragItem.type !== 'node' || !appId) {
        setDragItem(null);
        setDragOverId(null);
        return;
      }
      const updatedAreas: SitemapArea[] = app.sitemap.areas.map((area) => {
        const isSameArea = dragItem.areaId === targetAreaId;
        if (isSameArea && area.id === targetAreaId) {
          const nodes = [...area.nodes];
          const srcIdx = nodes.findIndex((n) => n.id === dragItem.id);
          if (srcIdx === -1 || srcIdx === targetIndex) return area;
          const [moved] = nodes.splice(srcIdx, 1);
          nodes.splice(targetIndex > srcIdx ? targetIndex - 1 : targetIndex, 0, moved);
          return {
            ...area,
            nodes: nodes.map((n, i) => ({ ...n, weight: (i + 1) * 10 })),
          };
        }
        if (area.id === dragItem.areaId) {
          return { ...area, nodes: area.nodes.filter((n) => n.id !== dragItem.id) };
        }
        if (area.id === targetAreaId) {
          const sourceArea = app.sitemap!.areas.find((a) => a.id === dragItem.areaId);
          const movedNode = sourceArea?.nodes.find((n) => n.id === dragItem.id);
          if (!movedNode) return area;
          const nodes = [...area.nodes];
          nodes.splice(targetIndex, 0, movedNode);
          return {
            ...area,
            nodes: nodes.map((n, i) => ({ ...n, weight: (i + 1) * 10 })),
          };
        }
        return area;
      });
      orderSitemapMut.mutate({ appId, sitemap: { areas: updatedAreas } });
      setDragItem(null);
      setDragOverId(null);
    },
    [app, dragItem, appId, orderSitemapMut],
  );

  // ── Form field updaters ────────────────────────────────────────────
  const updateAreaField = useCallback(
    <K extends keyof AreaFormState>(field: K, value: AreaFormState[K]) => {
      setAreaForm((prev) => ({ ...prev, [field]: value }));
    },
    [],
  );

  const updateNodeField = useCallback(
    <K extends keyof NodeFormState>(field: K, value: NodeFormState[K]) => {
      setNodeForm((prev) => ({ ...prev, [field]: value }));
    },
    [],
  );

  // ── Effects ─────────────────────────────────────────────────────────
  // Expand all areas on initial data load.
  useEffect(() => {
    if (app?.sitemap?.areas && expandedAreas.size === 0) {
      setExpandedAreas(new Set(app.sitemap.areas.map((a) => a.id)));
    }
  }, [app?.sitemap?.areas, expandedAreas.size]);

  // Reset editor if the currently-edited item disappears after a mutation.
  useEffect(() => {
    if (!app?.sitemap?.areas) return;
    if (editMode === 'area' && !isCreating && areaForm.id) {
      const exists = app.sitemap.areas.some((a) => a.id === areaForm.id);
      if (!exists) handleCancel();
    }
    if (editMode === 'node' && !isCreating && nodeForm.id) {
      const exists = app.sitemap.areas.some((a) =>
        a.nodes.some((n) => n.id === nodeForm.id),
      );
      if (!exists) handleCancel();
    }
  }, [app?.sitemap?.areas, editMode, isCreating, areaForm.id, nodeForm.id, handleCancel]);

  // ── Loading state ───────────────────────────────────────────────────
  if (isLoading) {
    return (
      <div className="flex items-center justify-center gap-3 p-12" role="status">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-t-transparent" />
        <span className="sr-only">Loading sitemap…</span>
      </div>
    );
  }

  // ── Error state ─────────────────────────────────────────────────────
  if (isError) {
    const errorMessage =
      error instanceof Error
        ? error.message
        : 'An unexpected error occurred while loading the application.';
    return (
      <div className="rounded border border-red-300 bg-red-50 p-4 text-red-800" role="alert">
        <p className="font-medium">Error loading application</p>
        <p className="mt-1 text-sm">{errorMessage}</p>
        <Link
          to="/admin/applications"
          className="mt-3 inline-flex items-center text-sm font-medium text-blue-600 hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          ← Back to applications
        </Link>
      </div>
    );
  }

  // ── Not found state ─────────────────────────────────────────────────
  if (!app) {
    return (
      <div className="rounded border border-yellow-300 bg-yellow-50 p-4 text-yellow-800" role="alert">
        <p className="font-medium">Application not found</p>
        <p className="mt-1 text-sm">The requested application could not be found.</p>
        <Link
          to="/admin/applications"
          className="mt-3 inline-flex items-center text-sm font-medium text-blue-600 hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          ← Back to applications
        </Link>
      </div>
    );
  }

  // ── Render helpers ──────────────────────────────────────────────────

  /** Renders nodes within an area, optionally grouped by SitemapGroup. */
  const renderNodes = (area: SitemapArea): React.ReactNode => {
    const sorted = [...area.nodes].sort(
      (a, b) => a.weight - b.weight || a.name.localeCompare(b.name),
    );

    if (area.showGroupNames && area.groups.length > 0) {
      const byGroup = new Map<string, SitemapNode[]>();
      for (const node of sorted) {
        const key = node.groupName || '';
        const list = byGroup.get(key) ?? [];
        list.push(node);
        byGroup.set(key, list);
      }
      const sortedGroups: SitemapGroup[] = [...area.groups].sort(
        (a: SitemapGroup, b: SitemapGroup) => a.weight - b.weight,
      );
      return (
        <>
          {sortedGroups.map((group: SitemapGroup) => (
            <div key={group.id}>
              <div className="bg-gray-50 px-3 py-1 text-xs font-semibold uppercase tracking-wide text-gray-500">
                {group.label}
              </div>
              {(byGroup.get(group.name) ?? []).map((node, idx) =>
                renderNodeRow(node, area.id, idx),
              )}
            </div>
          ))}
          {(byGroup.get('') ?? []).map((node, idx) =>
            renderNodeRow(node, area.id, sortedGroups.length + idx),
          )}
        </>
      );
    }

    return sorted.map((node, idx) => renderNodeRow(node, area.id, idx));
  };

  /** Renders a single node row within the tree panel. */
  const renderNodeRow = (
    node: SitemapNode,
    areaId: string,
    index: number,
  ): React.ReactElement => {
    const isSelected = editMode === 'node' && !isCreating && nodeForm.id === node.id;
    const isDragOver = dragOverId === `node-${node.id}`;
    return (
      <div
        key={node.id}
        role="treeitem"
        aria-selected={isSelected}
        className={[
          'flex items-center gap-2 border-b border-gray-100 px-3 py-1.5',
          'cursor-pointer text-sm',
          isSelected ? 'bg-blue-50 ring-1 ring-inset ring-blue-300' : 'hover:bg-gray-50',
          isDragOver ? 'border-t-2 border-t-blue-400' : '',
        ].join(' ')}
        draggable
        onDragStart={(e) => handleDragStart(e, 'node', node.id, areaId)}
        onDragOver={(e) => handleDragOver(e, `node-${node.id}`)}
        onDragLeave={handleDragLeave}
        onDrop={(e) => handleDropNode(e, areaId, index)}
        onDragEnd={handleDragEnd}
        onClick={() => selectNode(node, areaId)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            selectNode(node, areaId);
          }
        }}
        tabIndex={0}
      >
        <span className="cursor-grab" aria-label="Drag to reorder">
          <GripIcon />
        </span>
        {node.iconClass && (
          <span className={`${node.iconClass} text-sm text-gray-500`} aria-hidden="true" />
        )}
        <span className="min-w-0 flex-1 truncate">{node.label || node.name}</span>
        <span className="shrink-0 rounded bg-gray-100 px-1.5 py-0.5 text-xs text-gray-500">
          {nodeTypeLabel(node.type as number)}
        </span>
        <button
          type="button"
          className={BTN_ICON}
          onClick={(e) => {
            e.stopPropagation();
            setDeleteTarget({ type: 'node', id: node.id, areaId, label: node.label || node.name });
          }}
          aria-label={`Delete node ${node.label || node.name}`}
        >
          <TrashIcon />
        </button>
      </div>
    );
  };

  // ── Main render ─────────────────────────────────────────────────────
  return (
    <div className="space-y-6">
      {/* ── Page header ───────────────────────────────────────────────── */}
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          {app.iconClass && (
            <span
              className={`${app.iconClass} text-2xl`}
              style={app.color ? { color: app.color } : undefined}
              aria-hidden="true"
            />
          )}
          <div className="min-w-0">
            <p className="text-sm text-gray-500">
              <Link
                to="/admin/applications"
                className="hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              >
                Applications
              </Link>
            </p>
            <h1 className="truncate text-xl font-semibold text-gray-900">{app.label}</h1>
            <p className="text-sm text-gray-500">Sitemap</p>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <button type="button" className={BTN_SECONDARY} onClick={startCreateArea}>
            <PlusIcon />
            Add Area
          </button>
        </div>
      </header>

      {/* ── Sub-navigation tabs ──────────────────────────────────────── */}
      <nav aria-label="Application admin sections">
        <TabNav tabs={tabs} activeTabId="sitemap" onTabChange={handleTabChange} visibleTabs={4} />
      </nav>

      {/* ── Save error banner ────────────────────────────────────────── */}
      {saveError && (
        <div className="rounded border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800" role="alert">
          {saveError}
        </div>
      )}

      {/* ── Two-column layout: tree + editor ─────────────────────────── */}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[1fr_340px]">
        {/* ── Tree panel ───────────────────────────────────────────── */}
        <section aria-label="Sitemap tree" className="min-w-0">
          {sortedAreas.length === 0 && (
            <div className="rounded border border-dashed border-gray-300 p-8 text-center text-sm text-gray-500">
              No sitemap areas yet. Click &ldquo;Add Area&rdquo; to create one.
            </div>
          )}

          <div role="tree" aria-label="Sitemap areas">
            {sortedAreas.map((area, areaIndex) => {
              const isExpanded = expandedAreas.has(area.id);
              const isAreaSelected = editMode === 'area' && !isCreating && areaForm.id === area.id;
              const isAreaDragOver = dragOverId === `area-${area.id}`;
              return (
                <div
                  key={area.id}
                  role="treeitem"
                  aria-expanded={isExpanded}
                  aria-selected={isAreaSelected}
                  className={[
                    'mb-2 rounded border',
                    isAreaSelected ? 'border-blue-400 ring-1 ring-blue-200' : 'border-gray-200',
                    isAreaDragOver ? 'border-t-2 border-t-blue-500' : '',
                  ].join(' ')}
                  draggable
                  onDragStart={(e) => handleDragStart(e, 'area', area.id)}
                  onDragOver={(e) => handleDragOver(e, `area-${area.id}`)}
                  onDragLeave={handleDragLeave}
                  onDrop={(e) => handleDropArea(e, areaIndex)}
                  onDragEnd={handleDragEnd}
                >
                  {/* Area header */}
                  <div
                    className="flex items-center gap-2 rounded-t bg-gray-50 px-3 py-2"
                    role="presentation"
                  >
                    <span className="cursor-grab" aria-label="Drag to reorder area">
                      <GripIcon />
                    </span>
                    <button
                      type="button"
                      className="inline-flex items-center p-0.5 text-gray-500 hover:text-gray-700"
                      onClick={() => toggleExpand(area.id)}
                      aria-label={isExpanded ? 'Collapse area' : 'Expand area'}
                    >
                      {isExpanded ? <ChevronDownIcon /> : <ChevronRightIcon />}
                    </button>
                    {area.iconClass && (
                      <span
                        className={`${area.iconClass} text-sm`}
                        style={area.color ? { color: area.color } : undefined}
                        aria-hidden="true"
                      />
                    )}
                    <button
                      type="button"
                      className="min-w-0 flex-1 truncate text-start text-sm font-medium text-gray-800 hover:text-blue-700"
                      onClick={() => selectArea(area)}
                    >
                      {area.label || area.name}
                    </button>
                    <span className="shrink-0 text-xs text-gray-400">w:{area.weight}</span>
                    <button
                      type="button"
                      className={BTN_ICON}
                      onClick={() => startCreateNode(area.id)}
                      aria-label={`Add node to ${area.label || area.name}`}
                    >
                      <PlusIcon />
                    </button>
                    <button
                      type="button"
                      className={BTN_ICON}
                      onClick={() =>
                        setDeleteTarget({
                          type: 'area',
                          id: area.id,
                          label: area.label || area.name,
                        })
                      }
                      aria-label={`Delete area ${area.label || area.name}`}
                    >
                      <TrashIcon />
                    </button>
                  </div>

                  {/* Area body — nodes list */}
                  {isExpanded && (
                    <div role="group" aria-label={`Nodes in ${area.label || area.name}`}>
                      {area.nodes.length === 0 ? (
                        <div className="px-4 py-3 text-center text-xs text-gray-400">
                          No nodes in this area.
                        </div>
                      ) : (
                        renderNodes(area)
                      )}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </section>

        {/* ── Property editor panel ────────────────────────────────── */}
        <aside aria-label="Property editor" className="min-w-0">
          {editMode === null && !isCreating && (
            <div className="rounded border border-dashed border-gray-300 p-6 text-center text-sm text-gray-500">
              Select an area or node from the tree to edit its properties.
            </div>
          )}

          {/* Area property editor */}
          {editMode === 'area' && (
            <div className="rounded border border-gray-200 bg-white">
              <div className="border-b border-gray-200 bg-gray-50 px-4 py-2 text-sm font-semibold text-gray-700">
                {isCreating ? 'New Area' : 'Edit Area'}
              </div>
              <div className="space-y-3 p-4">
                <div>
                  <label htmlFor="area-name" className={LABEL_CLASSES}>
                    Name <span className="text-red-500">*</span>
                  </label>
                  <input
                    id="area-name"
                    type="text"
                    className={INPUT_CLASSES}
                    value={areaForm.name}
                    onChange={(e) => updateAreaField('name', e.target.value)}
                    required
                    placeholder="url-safe-slug"
                  />
                </div>
                <div>
                  <label htmlFor="area-label" className={LABEL_CLASSES}>
                    Label <span className="text-red-500">*</span>
                  </label>
                  <input
                    id="area-label"
                    type="text"
                    className={INPUT_CLASSES}
                    value={areaForm.label}
                    onChange={(e) => updateAreaField('label', e.target.value)}
                    required
                    placeholder="Area Display Name"
                  />
                </div>
                <div>
                  <label htmlFor="area-description" className={LABEL_CLASSES}>
                    Description
                  </label>
                  <textarea
                    id="area-description"
                    className={INPUT_CLASSES}
                    value={areaForm.description}
                    onChange={(e) => updateAreaField('description', e.target.value)}
                    rows={2}
                  />
                </div>
                <div>
                  <label htmlFor="area-icon" className={LABEL_CLASSES}>
                    Icon Class
                  </label>
                  <input
                    id="area-icon"
                    type="text"
                    className={INPUT_CLASSES}
                    value={areaForm.iconClass}
                    onChange={(e) => updateAreaField('iconClass', e.target.value)}
                    placeholder="fa fa-folder"
                  />
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label htmlFor="area-weight" className={LABEL_CLASSES}>
                      Weight
                    </label>
                    <input
                      id="area-weight"
                      type="number"
                      className={INPUT_CLASSES}
                      value={areaForm.weight}
                      onChange={(e) => updateAreaField('weight', Number(e.target.value) || 0)}
                    />
                  </div>
                  <div>
                    <label htmlFor="area-color" className={LABEL_CLASSES}>
                      Color
                    </label>
                    <input
                      id="area-color"
                      type="color"
                      className="h-9 w-full cursor-pointer rounded border border-gray-300"
                      value={areaForm.color || '#999999'}
                      onChange={(e) => updateAreaField('color', e.target.value)}
                    />
                  </div>
                </div>
                <div className="flex items-center gap-2">
                  <input
                    id="area-show-groups"
                    type="checkbox"
                    className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    checked={areaForm.showGroupNames}
                    onChange={(e) => updateAreaField('showGroupNames', e.target.checked)}
                  />
                  <label htmlFor="area-show-groups" className="text-sm text-gray-700">
                    Show group names
                  </label>
                </div>
                <div className="flex items-center gap-2 border-t border-gray-200 pt-3">
                  <button
                    type="button"
                    className={BTN_PRIMARY}
                    onClick={handleSave}
                    disabled={isSaving || !areaForm.name.trim() || !areaForm.label.trim()}
                  >
                    {isSaving ? 'Saving…' : isCreating ? 'Create' : 'Save'}
                  </button>
                  <button type="button" className={BTN_SECONDARY} onClick={handleCancel}>
                    Cancel
                  </button>
                </div>
              </div>
            </div>
          )}

          {/* Node property editor */}
          {editMode === 'node' && (
            <div className="rounded border border-gray-200 bg-white">
              <div className="border-b border-gray-200 bg-gray-50 px-4 py-2 text-sm font-semibold text-gray-700">
                {isCreating ? 'New Node' : 'Edit Node'}
              </div>
              <div className="space-y-3 p-4">
                <div>
                  <label htmlFor="node-name" className={LABEL_CLASSES}>
                    Name <span className="text-red-500">*</span>
                  </label>
                  <input
                    id="node-name"
                    type="text"
                    className={INPUT_CLASSES}
                    value={nodeForm.name}
                    onChange={(e) => updateNodeField('name', e.target.value)}
                    required
                    placeholder="url-safe-slug"
                  />
                </div>
                <div>
                  <label htmlFor="node-label" className={LABEL_CLASSES}>
                    Label <span className="text-red-500">*</span>
                  </label>
                  <input
                    id="node-label"
                    type="text"
                    className={INPUT_CLASSES}
                    value={nodeForm.label}
                    onChange={(e) => updateNodeField('label', e.target.value)}
                    required
                    placeholder="Node Display Name"
                  />
                </div>
                <div>
                  <label htmlFor="node-icon" className={LABEL_CLASSES}>
                    Icon Class
                  </label>
                  <input
                    id="node-icon"
                    type="text"
                    className={INPUT_CLASSES}
                    value={nodeForm.iconClass}
                    onChange={(e) => updateNodeField('iconClass', e.target.value)}
                    placeholder="fa fa-file"
                  />
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label htmlFor="node-weight" className={LABEL_CLASSES}>
                      Weight
                    </label>
                    <input
                      id="node-weight"
                      type="number"
                      className={INPUT_CLASSES}
                      value={nodeForm.weight}
                      onChange={(e) => updateNodeField('weight', Number(e.target.value) || 0)}
                    />
                  </div>
                  <div>
                    <label htmlFor="node-type" className={LABEL_CLASSES}>
                      Type
                    </label>
                    <select
                      id="node-type"
                      className={INPUT_CLASSES}
                      value={nodeForm.type}
                      onChange={(e) => updateNodeField('type', Number(e.target.value))}
                    >
                      {NODE_TYPE_OPTIONS.map((opt) => (
                        <option key={opt.value} value={opt.value}>
                          {opt.label}
                        </option>
                      ))}
                    </select>
                  </div>
                </div>

                {/* URL field — visible for URL-type nodes */}
                {nodeForm.type === NODE_TYPE_URL && (
                  <div>
                    <label htmlFor="node-url" className={LABEL_CLASSES}>
                      URL
                    </label>
                    <input
                      id="node-url"
                      type="text"
                      className={INPUT_CLASSES}
                      value={nodeForm.url}
                      onChange={(e) => updateNodeField('url', e.target.value)}
                      placeholder="https://example.com"
                    />
                  </div>
                )}

                {/* Entity selection — visible for EntityList-type nodes */}
                {nodeForm.type === NODE_TYPE_ENTITY_LIST && (
                  <div>
                    <label htmlFor="node-entity" className={LABEL_CLASSES}>
                      Entity
                    </label>
                    <select
                      id="node-entity"
                      className={INPUT_CLASSES}
                      value={nodeForm.entityId}
                      onChange={(e) => updateNodeField('entityId', e.target.value)}
                    >
                      <option value="">— Select entity —</option>
                      {entities
                        ?.slice()
                        .sort((a, b) => a.name.localeCompare(b.name))
                        .map((entity) => (
                          <option key={entity.id} value={entity.id}>
                            {entity.label} ({entity.name})
                          </option>
                        ))}
                    </select>
                  </div>
                )}

                <div>
                  <label htmlFor="node-group" className={LABEL_CLASSES}>
                    Group Name
                  </label>
                  <input
                    id="node-group"
                    type="text"
                    className={INPUT_CLASSES}
                    value={nodeForm.groupName}
                    onChange={(e) => updateNodeField('groupName', e.target.value)}
                    placeholder="(optional)"
                  />
                </div>

                <div className="flex items-center gap-2 border-t border-gray-200 pt-3">
                  <button
                    type="button"
                    className={BTN_PRIMARY}
                    onClick={handleSave}
                    disabled={isSaving || !nodeForm.name.trim() || !nodeForm.label.trim()}
                  >
                    {isSaving ? 'Saving…' : isCreating ? 'Create' : 'Save'}
                  </button>
                  <button type="button" className={BTN_SECONDARY} onClick={handleCancel}>
                    Cancel
                  </button>
                </div>
              </div>
            </div>
          )}
        </aside>
      </div>

      {/* ── Delete confirmation modal ─────────────────────────────────── */}
      <Modal
        isVisible={deleteTarget !== null}
        title={`Delete ${deleteTarget?.type === 'area' ? 'Area' : 'Node'}`}
        onClose={() => setDeleteTarget(null)}
        footer={
          <div className="flex items-center justify-end gap-2">
            <button
              type="button"
              className={BTN_SECONDARY}
              onClick={() => setDeleteTarget(null)}
              disabled={isDeleting}
            >
              Cancel
            </button>
            <button
              type="button"
              className={BTN_DANGER}
              onClick={confirmDelete}
              disabled={isDeleting}
            >
              {isDeleting ? 'Deleting…' : 'Delete'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-700">
          Are you sure you want to delete{' '}
          <strong className="font-semibold">{deleteTarget?.label ?? 'this item'}</strong>?
          {deleteTarget?.type === 'area' && (
            <span className="mt-1 block text-xs text-red-600">
              All nodes within this area will also be deleted.
            </span>
          )}
        </p>
      </Modal>
    </div>
  );
}
