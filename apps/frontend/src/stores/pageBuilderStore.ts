/**
 * Page Builder Zustand Store — `apps/frontend/src/stores/pageBuilderStore.ts`
 *
 * Zustand 5 store for page builder admin-mode state management.
 *
 * Replaces:
 *  - `PageComponentContext.cs`  — Runtime component context (Node, Options,
 *                                  Mode, DataModel, Items bag)
 *  - `PageBodyNode.cs`          — Recursive page-body tree structure
 *  - `PageService.cs`           — Tree manipulation CRUD (CreatePageBodyNode,
 *                                  UpdatePageBodyNode, DeletePageBodyNodeInternal
 *                                  cascade delete, GetPageBody tree reconstruction)
 *  - `ComponentMode.cs`         — Display / Design / Options / Help enum
 *  - `PageComponentLibraryService.cs` — Component catalog / picker state
 *
 * This store manages **client-only** page builder editing state. Actual
 * page/component data fetching is handled by TanStack Query hooks — the
 * store receives a pre-fetched tree via `setComponentTree()` and all edits
 * happen on a mutable copy. Persisting changes back to the server is the
 * responsibility of TanStack Query mutations, NOT this store.
 *
 * No localStorage persistence — page builder state is ephemeral and
 * scoped to the active editing session.
 */

import { create } from 'zustand';
import type { PageBodyNode } from '../types/page';
import { ComponentMode } from '../types/page';

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * Drag-and-drop state for the page builder canvas.
 *
 * Tracks which node is being dragged, the current drop target, and the
 * spatial relationship of the cursor to the target (before / after / inside).
 */
export interface DragState {
  /** Whether a drag operation is currently in progress */
  isDragging: boolean;
  /** Identifier of the node being dragged; `null` when idle */
  draggedNodeId: string | null;
  /** Identifier of the prospective drop-target node; `null` when idle */
  dropTargetNodeId: string | null;
  /** Spatial relationship of the cursor to the drop target */
  dropPosition: 'before' | 'after' | 'inside' | null;
}

/**
 * Complete page-builder store shape including state properties and actions.
 *
 * State properties mirror the C# `PageComponentContext` runtime data bag
 * (selected node, mode, options) combined with the `PageBodyNode` tree
 * and drag-and-drop / component-catalog UI state.
 *
 * Actions replicate `PageService.cs` tree manipulation operations
 * (CreatePageBodyNode, UpdatePageBodyNode, DeletePageBodyNodeInternal)
 * adapted for immutable Zustand updates on a recursive tree.
 */
export interface PageBuilderState {
  // -- State properties --------------------------------------------------

  /** Whether the page builder is in edit (design) mode */
  isEditMode: boolean;

  /** Mutable copy of the page body tree fetched via TanStack Query */
  componentTree: PageBodyNode[];

  /**
   * Currently selected node identifier; `null` when no node is selected.
   * Replaces `PageComponentContext.Node`.
   */
  selectedNodeId: string | null;

  /**
   * Rendering mode for the selected component.
   * Matches C# `ComponentMode` enum: Display=1, Design=2, Options=3, Help=4.
   * Replaces `PageComponentContext.Mode`.
   */
  selectedMode: ComponentMode;

  /**
   * Parsed options of the selected node as a key-value map.
   * Replaces `PageComponentContext.Options` (JObject).
   */
  selectedNodeOptions: Record<string, unknown> | null;

  /** Drag-and-drop state for the page builder canvas */
  dragState: DragState;

  /**
   * Whether the component catalog / picker panel is open.
   * Replaces the SDK's `pb-manager` StencilJS component visibility.
   */
  componentCatalogOpen: boolean;

  /** Dirty flag — `true` when unsaved edits exist on `componentTree` */
  isDirty: boolean;

  // -- Actions -----------------------------------------------------------

  /** Toggle the page builder between edit and display mode */
  setEditMode: (enabled: boolean) => void;

  /**
   * Replace the entire component tree. Called after TanStack Query
   * fetches the page body so the builder has a mutable working copy.
   */
  setComponentTree: (tree: PageBodyNode[]) => void;

  /**
   * Select a node by identifier; `null` deselects.
   * Parses the node's `options` JSON into `selectedNodeOptions`.
   */
  selectNode: (nodeId: string | null) => void;

  /** Set the rendering mode for the selected component */
  setSelectedMode: (mode: ComponentMode) => void;

  /**
   * Update the options for a specific node. Mirrors
   * `PageService.UpdatePageBodyNodeOptions`. Serialises the options
   * map back to the node's `options` JSON string and marks dirty.
   */
  updateNodeOptions: (nodeId: string, options: Record<string, unknown>) => void;

  /**
   * Insert a new node into the tree. Mirrors `PageService.CreatePageBodyNode`:
   *  - Generates a UUID via `crypto.randomUUID()`
   *  - Places the node under `parentId` / `containerId` at `weight`
   *  - Auto-assigns weight if omitted
   */
  addNode: (
    parentId: string | null,
    containerId: string,
    componentName: string,
    weight?: number,
  ) => void;

  /**
   * Cascade-remove a node and all descendants. Mirrors
   * `PageService.DeletePageBodyNodeInternal` queue/stack traversal.
   */
  removeNode: (nodeId: string) => void;

  /**
   * Re-parent a node in the tree (drag-and-drop). Updates `parentId`,
   * `containerId`, and `weight` on the moved node.
   */
  moveNode: (
    nodeId: string,
    newParentId: string | null,
    newContainerId: string,
    newWeight: number,
  ) => void;

  /**
   * Swap the weight of a node with its adjacent sibling.
   * Mirrors the weight-based ordering from `PageBodyNode.Weight`.
   */
  reorderNode: (nodeId: string, direction: 'up' | 'down') => void;

  /** Partially update drag-and-drop state */
  setDragState: (state: Partial<DragState>) => void;

  /** Reset drag-and-drop state to idle defaults */
  resetDragState: () => void;

  /** Toggle the component catalog / picker panel */
  toggleComponentCatalog: () => void;

  /** Clear the dirty flag (called after a successful save) */
  markClean: () => void;

  /**
   * Reset all page builder state to defaults. Called when exiting
   * edit mode or navigating away from the page builder.
   */
  resetPageBuilder: () => void;
}

// ---------------------------------------------------------------------------
// Defaults
// ---------------------------------------------------------------------------

/** Idle drag state — no drag operation in progress */
const initialDragState: DragState = {
  isDragging: false,
  draggedNodeId: null,
  dropTargetNodeId: null,
  dropPosition: null,
};

// ---------------------------------------------------------------------------
// Tree Traversal Helpers
// ---------------------------------------------------------------------------

/**
 * Recursively search the `PageBodyNode` tree for a node with the given `id`.
 *
 * @param tree  Root-level node array to search
 * @param id    Target node identifier
 * @returns     The matching node, or `null` if not found
 */
function findNodeById(
  tree: PageBodyNode[],
  id: string,
): PageBodyNode | null {
  for (const node of tree) {
    if (node.id === id) {
      return node;
    }
    if (node.nodes.length > 0) {
      const found = findNodeById(node.nodes, id);
      if (found) {
        return found;
      }
    }
  }
  return null;
}

/**
 * Create a deep clone of the component tree with a specific node removed.
 *
 * This mirrors `PageService.DeletePageBodyNodeInternal`'s cascade-delete
 * behaviour: removing a node also removes its entire subtree because the
 * child `nodes` array is simply excluded when the parent is filtered out.
 *
 * @param tree  Root-level node array
 * @param id    Identifier of the node to remove
 * @returns     New tree with the target node (and its descendants) removed
 */
function removeNodeFromTree(
  tree: PageBodyNode[],
  id: string,
): PageBodyNode[] {
  return tree
    .filter((node) => node.id !== id)
    .map((node) => ({
      ...node,
      nodes: removeNodeFromTree(node.nodes, id),
    }));
}

/**
 * Recursively update a node's properties within the tree, producing a new
 * tree reference (immutable update).
 *
 * @param tree    Root-level node array
 * @param id      Target node identifier
 * @param updater Callback that receives the matching node and returns a
 *                new node with the desired changes
 * @returns       New tree with the target node updated
 */
function updateNodeInTree(
  tree: PageBodyNode[],
  id: string,
  updater: (node: PageBodyNode) => PageBodyNode,
): PageBodyNode[] {
  return tree.map((node) => {
    if (node.id === id) {
      return updater(node);
    }
    if (node.nodes.length > 0) {
      return {
        ...node,
        nodes: updateNodeInTree(node.nodes, id, updater),
      };
    }
    return node;
  });
}

/**
 * Insert a child node into the tree at the specified parent location.
 *
 * If `parentId` is `null` the node is appended at root level. Otherwise
 * the function walks the tree recursively to find the parent and inserts
 * the new node into its `nodes` array sorted by weight.
 *
 * @param tree     Root-level node array
 * @param parentId Target parent identifier (`null` for root)
 * @param child    The new `PageBodyNode` to insert
 * @returns        New tree with the child inserted
 */
function insertNodeInTree(
  tree: PageBodyNode[],
  parentId: string | null,
  child: PageBodyNode,
): PageBodyNode[] {
  if (parentId === null) {
    // Insert at root level, sorted by weight
    const newTree = [...tree, child];
    newTree.sort((a, b) => a.weight - b.weight);
    return newTree;
  }

  return tree.map((node) => {
    if (node.id === parentId) {
      const updatedNodes = [...node.nodes, child];
      updatedNodes.sort((a, b) => a.weight - b.weight);
      return { ...node, nodes: updatedNodes };
    }
    if (node.nodes.length > 0) {
      return {
        ...node,
        nodes: insertNodeInTree(node.nodes, parentId, child),
      };
    }
    return node;
  });
}

/**
 * Compute the next available weight for a container within a parent node.
 *
 * Mirrors the weight auto-increment behaviour of `PageService.CreatePageBodyNode`
 * where a new node receives weight = max(existing siblings' weight) + 1.
 *
 * @param tree        Root-level node array
 * @param parentId    Parent node identifier (`null` for root)
 * @param containerId Container slot within the parent
 * @returns           The next available weight value
 */
function getNextWeight(
  tree: PageBodyNode[],
  parentId: string | null,
  containerId: string,
): number {
  let siblings: PageBodyNode[];

  if (parentId === null) {
    siblings = tree.filter((n) => n.containerId === containerId);
  } else {
    const parent = findNodeById(tree, parentId);
    siblings = parent
      ? parent.nodes.filter((n) => n.containerId === containerId)
      : [];
  }

  if (siblings.length === 0) {
    return 1;
  }

  return Math.max(...siblings.map((n) => n.weight)) + 1;
}

/**
 * Safely parse a JSON `options` string into a key-value map.
 *
 * @param options  Raw JSON string from `PageBodyNode.options`
 * @returns        Parsed object, or `null` if the string is empty / invalid
 */
function parseNodeOptions(
  options: string,
): Record<string, unknown> | null {
  if (!options || options.trim() === '') {
    return null;
  }
  try {
    const parsed: unknown = JSON.parse(options);
    if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
    return null;
  } catch {
    return null;
  }
}

// ---------------------------------------------------------------------------
// Store
// ---------------------------------------------------------------------------

/**
 * Zustand 5 store for the page builder admin interface.
 *
 * Usage:
 * ```tsx
 * import { usePageBuilderStore, useIsEditMode } from '../stores/pageBuilderStore';
 *
 * // Full store access
 * const { componentTree, addNode, removeNode } = usePageBuilderStore();
 *
 * // Granular selector
 * const isEditMode = useIsEditMode();
 * ```
 */
export const usePageBuilderStore = create<PageBuilderState>()((set, get) => ({
  // -- Initial state -----------------------------------------------------

  isEditMode: false,
  componentTree: [],
  selectedNodeId: null,
  selectedMode: ComponentMode.Display,
  selectedNodeOptions: null,
  dragState: { ...initialDragState },
  componentCatalogOpen: false,
  isDirty: false,

  // -- Actions -----------------------------------------------------------

  setEditMode: (enabled: boolean): void => {
    set({
      isEditMode: enabled,
      // Deselect any node when toggling mode
      selectedNodeId: enabled ? get().selectedNodeId : null,
      selectedNodeOptions: enabled ? get().selectedNodeOptions : null,
      selectedMode: enabled ? get().selectedMode : ComponentMode.Display,
    });
  },

  setComponentTree: (tree: PageBodyNode[]): void => {
    set({
      componentTree: tree,
      // Clear selection when replacing the tree entirely
      selectedNodeId: null,
      selectedNodeOptions: null,
      selectedMode: ComponentMode.Display,
      isDirty: false,
    });
  },

  selectNode: (nodeId: string | null): void => {
    if (nodeId === null) {
      set({
        selectedNodeId: null,
        selectedNodeOptions: null,
        selectedMode: ComponentMode.Display,
      });
      return;
    }

    const { componentTree } = get();
    const node = findNodeById(componentTree, nodeId);

    if (!node) {
      // Node not found — clear selection
      set({
        selectedNodeId: null,
        selectedNodeOptions: null,
        selectedMode: ComponentMode.Display,
      });
      return;
    }

    set({
      selectedNodeId: node.id,
      selectedNodeOptions: parseNodeOptions(node.options),
      // Preserve the current mode if we're re-selecting the same node
      selectedMode:
        get().selectedNodeId === nodeId
          ? get().selectedMode
          : ComponentMode.Display,
    });
  },

  setSelectedMode: (mode: ComponentMode): void => {
    set({ selectedMode: mode });
  },

  updateNodeOptions: (
    nodeId: string,
    options: Record<string, unknown>,
  ): void => {
    const { componentTree, selectedNodeId } = get();

    const serialised = JSON.stringify(options);

    const updatedTree = updateNodeInTree(componentTree, nodeId, (node) => ({
      ...node,
      options: serialised,
    }));

    set({
      componentTree: updatedTree,
      isDirty: true,
      // If the updated node is currently selected, refresh selectedNodeOptions
      selectedNodeOptions: selectedNodeId === nodeId ? options : get().selectedNodeOptions,
    });
  },

  addNode: (
    parentId: string | null,
    containerId: string,
    componentName: string,
    weight?: number,
  ): void => {
    const { componentTree } = get();

    const resolvedWeight =
      weight !== undefined && weight !== null
        ? weight
        : getNextWeight(componentTree, parentId, containerId);

    // Generate a default pageId from the first root node, or use an empty GUID
    const pageId =
      componentTree.length > 0
        ? componentTree[0].pageId
        : '00000000-0000-0000-0000-000000000000';

    const newNode: PageBodyNode = {
      id: crypto.randomUUID(),
      parentId: parentId,
      pageId: pageId,
      nodeId: null,
      containerId: containerId,
      weight: resolvedWeight,
      componentName: componentName,
      options: '',
      nodes: [],
    };

    const updatedTree = insertNodeInTree(componentTree, parentId, newNode);

    set({
      componentTree: updatedTree,
      isDirty: true,
    });
  },

  removeNode: (nodeId: string): void => {
    const { componentTree, selectedNodeId } = get();

    const updatedTree = removeNodeFromTree(componentTree, nodeId);

    // If the removed node was selected, clear the selection
    const wasSelected = selectedNodeId === nodeId;

    // Also check if the selected node was a descendant of the removed node
    const selectedStillExists = selectedNodeId
      ? findNodeById(updatedTree, selectedNodeId) !== null
      : false;

    set({
      componentTree: updatedTree,
      isDirty: true,
      selectedNodeId:
        wasSelected || !selectedStillExists ? null : selectedNodeId,
      selectedNodeOptions:
        wasSelected || !selectedStillExists ? null : get().selectedNodeOptions,
      selectedMode:
        wasSelected || !selectedStillExists
          ? ComponentMode.Display
          : get().selectedMode,
    });
  },

  moveNode: (
    nodeId: string,
    newParentId: string | null,
    newContainerId: string,
    newWeight: number,
  ): void => {
    const { componentTree } = get();

    // Step 1: Find the node in its current position
    const nodeToMove = findNodeById(componentTree, nodeId);
    if (!nodeToMove) {
      return;
    }

    // Step 2: Remove from old position
    const treeWithoutNode = removeNodeFromTree(componentTree, nodeId);

    // Step 3: Create the moved node with updated parent/container/weight
    const movedNode: PageBodyNode = {
      ...nodeToMove,
      parentId: newParentId,
      containerId: newContainerId,
      weight: newWeight,
      // Preserve the subtree — child nodes move with the parent
      nodes: nodeToMove.nodes,
    };

    // Step 4: Insert at new position
    const updatedTree = insertNodeInTree(
      treeWithoutNode,
      newParentId,
      movedNode,
    );

    set({
      componentTree: updatedTree,
      isDirty: true,
    });
  },

  reorderNode: (nodeId: string, direction: 'up' | 'down'): void => {
    const { componentTree } = get();

    /**
     * Find the node and its siblings (nodes with the same parentId and
     * containerId), then swap weights with the adjacent sibling.
     *
     * This mirrors the weight-based ordering from `PageBodyNode.Weight`
     * in the monolith, where reordering is achieved by swapping weights.
     */
    const targetNode = findNodeById(componentTree, nodeId);
    if (!targetNode) {
      return;
    }

    const { parentId, containerId } = targetNode;

    // Collect siblings: nodes with the same parentId and containerId
    let siblings: PageBodyNode[];
    if (parentId === null) {
      siblings = componentTree.filter(
        (n) => n.parentId === null && n.containerId === containerId,
      );
    } else {
      const parent = findNodeById(componentTree, parentId);
      if (!parent) {
        return;
      }
      siblings = parent.nodes.filter(
        (n) => n.containerId === containerId,
      );
    }

    // Sort siblings by weight to determine adjacency
    const sorted = [...siblings].sort((a, b) => a.weight - b.weight);
    const currentIndex = sorted.findIndex((n) => n.id === nodeId);

    if (currentIndex === -1) {
      return;
    }

    const swapIndex =
      direction === 'up' ? currentIndex - 1 : currentIndex + 1;

    if (swapIndex < 0 || swapIndex >= sorted.length) {
      // Already at the boundary — nothing to swap
      return;
    }

    const swapNode = sorted[swapIndex];

    // Swap the weights of the two nodes
    let updatedTree = updateNodeInTree(componentTree, nodeId, (node) => ({
      ...node,
      weight: swapNode.weight,
    }));

    updatedTree = updateNodeInTree(updatedTree, swapNode.id, (node) => ({
      ...node,
      weight: targetNode.weight,
    }));

    set({
      componentTree: updatedTree,
      isDirty: true,
    });
  },

  setDragState: (partial: Partial<DragState>): void => {
    set((state) => ({
      dragState: { ...state.dragState, ...partial },
    }));
  },

  resetDragState: (): void => {
    set({ dragState: { ...initialDragState } });
  },

  toggleComponentCatalog: (): void => {
    set((state) => ({
      componentCatalogOpen: !state.componentCatalogOpen,
    }));
  },

  markClean: (): void => {
    set({ isDirty: false });
  },

  resetPageBuilder: (): void => {
    set({
      isEditMode: false,
      componentTree: [],
      selectedNodeId: null,
      selectedMode: ComponentMode.Display,
      selectedNodeOptions: null,
      dragState: { ...initialDragState },
      componentCatalogOpen: false,
      isDirty: false,
    });
  },
}));

// ---------------------------------------------------------------------------
// Typed Selectors — granular subscriptions for common access patterns
// ---------------------------------------------------------------------------

/** Select the currently selected node identifier */
export const useSelectedNode = (): string | null =>
  usePageBuilderStore((state) => state.selectedNodeId);

/** Select whether the page builder is in edit mode */
export const useIsEditMode = (): boolean =>
  usePageBuilderStore((state) => state.isEditMode);

/** Select the full component tree */
export const useComponentTree = (): PageBodyNode[] =>
  usePageBuilderStore((state) => state.componentTree);

/** Select the dirty flag (unsaved changes indicator) */
export const useIsDirty = (): boolean =>
  usePageBuilderStore((state) => state.isDirty);
