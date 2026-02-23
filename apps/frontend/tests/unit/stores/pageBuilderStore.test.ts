/**
 * Vitest Unit Tests — `pageBuilderStore` (Zustand 5 Page Builder Store)
 *
 * Comprehensive test suite for the page builder Zustand store that replaces
 * the monolith's server-side page body tree manipulation logic and runtime
 * component state:
 *
 *  - `PageService.cs`           — Tree manipulation CRUD: CreatePageBodyNode,
 *                                  UpdatePageBodyNode, DeletePageBodyNodeInternal
 *                                  (cascade delete), GetPageBody tree reconstruction
 *  - `PageComponentContext.cs`   — Runtime component context: Node, Options,
 *                                  Mode, DataModel
 *  - `PageBodyNode.cs`           — Recursive page-body tree structure (Id, ParentId,
 *                                  PageId, NodeId, ContainerId, Weight, ComponentName,
 *                                  Options, Nodes)
 *  - `ComponentMode.cs`          — Display(1), Design(2), Options(3), Help(4) enum
 *
 * All tests use the `usePageBuilderStore.getState()` / `.setState()` pattern
 * for direct state manipulation — no React rendering is required.
 *
 * @see apps/frontend/src/stores/pageBuilderStore.ts
 * @see apps/frontend/src/types/page.ts
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { usePageBuilderStore } from '../../../src/stores/pageBuilderStore';
import type { PageBodyNode } from '../../../src/types/page';
import { ComponentMode } from '../../../src/types/page';

// ---------------------------------------------------------------------------
// crypto.randomUUID Mocking
// ---------------------------------------------------------------------------

/**
 * Deterministic UUID counter for predictable node ID generation in addNode
 * tests. The real store uses `crypto.randomUUID()` which is not available in
 * the jsdom test environment and would produce non-deterministic values.
 */
let uuidCounter = 0;
vi.stubGlobal('crypto', {
  randomUUID: () => `mock-uuid-${++uuidCounter}`,
});

// ---------------------------------------------------------------------------
// Test Data Fixtures
// ---------------------------------------------------------------------------

/**
 * Root-level node fixture — represents a `PcRow` (12-column grid layout)
 * at the top of the page body tree. Mirrors the monolith's
 * `PageBodyNode.cs` with `ParentId = null`.
 */
const mockRootNode: PageBodyNode = {
  id: 'node-root-1',
  parentId: null,
  pageId: 'page-uuid-1',
  nodeId: null,
  containerId: '00000000-0000-0000-0000-000000000000',
  weight: 1,
  componentName: 'PcRow',
  options: '{"class":""}',
  nodes: [],
};

/**
 * Child node fixture — a `PcFieldText` component nested under the root node.
 * Weight 1 indicates it is the first component in `container-1`.
 */
const mockChildNode: PageBodyNode = {
  id: 'node-child-1',
  parentId: 'node-root-1',
  pageId: 'page-uuid-1',
  nodeId: null,
  containerId: 'container-1',
  weight: 1,
  componentName: 'PcFieldText',
  options: '{"label":"Name","name":"name"}',
  nodes: [],
};

/**
 * Second child node fixture — a `PcFieldDate` component at weight 2 in the
 * same container as mockChildNode, for ordering/reorder tests.
 */
const mockChildNode2: PageBodyNode = {
  id: 'node-child-2',
  parentId: 'node-root-1',
  pageId: 'page-uuid-1',
  nodeId: null,
  containerId: 'container-1',
  weight: 2,
  componentName: 'PcFieldDate',
  options: '{"label":"Created","name":"created_on"}',
  nodes: [],
};

/**
 * Tree fixture: root node with two children — the primary fixture for
 * tree manipulation tests (remove, move, update).
 */
const mockTreeWithChildren: PageBodyNode[] = [
  {
    ...mockRootNode,
    nodes: [
      { ...mockChildNode },
      { ...mockChildNode2 },
    ],
  },
];

// ---------------------------------------------------------------------------
// Test Suite
// ---------------------------------------------------------------------------

describe('pageBuilderStore', () => {
  /**
   * CRITICAL: Reset store state before every test to prevent state leaks.
   * This mirrors the monolith's per-request scoping where each HTTP request
   * gets a fresh `PageComponentContext` via `ErpMiddleware`.
   */
  beforeEach(() => {
    uuidCounter = 0;
    usePageBuilderStore.setState({
      isEditMode: false,
      componentTree: [],
      selectedNodeId: null,
      selectedMode: ComponentMode.Display, // 1
      selectedNodeOptions: null,
      dragState: {
        isDragging: false,
        draggedNodeId: null,
        dropTargetNodeId: null,
        dropPosition: null,
      },
      componentCatalogOpen: false,
      isDirty: false,
    });
  });

  // -------------------------------------------------------------------------
  // componentTree State
  // -------------------------------------------------------------------------

  describe('componentTree state', () => {
    it('initial componentTree is empty array', () => {
      const { componentTree } = usePageBuilderStore.getState();
      expect(componentTree).toEqual([]);
      expect(componentTree).toHaveLength(0);
    });

    it('setComponentTree sets the full component tree', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree(structuredClone(mockTreeWithChildren));

      const state = usePageBuilderStore.getState();
      expect(state.componentTree).toHaveLength(1);
      expect(state.componentTree[0].nodes).toHaveLength(2);
      expect(state.componentTree[0].componentName).toBe('PcRow');
      expect(state.componentTree[0].nodes[0].componentName).toBe('PcFieldText');
      expect(state.componentTree[0].nodes[1].componentName).toBe('PcFieldDate');
      // Setting tree is a load operation, NOT an edit — isDirty must remain false
      expect(state.isDirty).toBe(false);
    });
  });

  // -------------------------------------------------------------------------
  // addNode Action
  // -------------------------------------------------------------------------

  describe('addNode action', () => {
    it('addNode inserts new node into tree root', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree([{ ...mockRootNode, nodes: [] }]);

      usePageBuilderStore.getState().addNode(
        null,
        '00000000-0000-0000-0000-000000000000',
        'PcSection',
        2,
      );

      const state = usePageBuilderStore.getState();
      expect(state.componentTree).toHaveLength(2);

      const newNode = state.componentTree.find(
        (n) => n.componentName === 'PcSection',
      );
      expect(newNode).toBeDefined();
      expect(newNode!.componentName).toBe('PcSection');
      expect(newNode!.weight).toBe(2);
      expect(newNode!.id).toBeTruthy();
      expect(newNode!.parentId).toBeNull();
      expect(newNode!.nodes).toEqual([]);
      expect(state.isDirty).toBe(true);
    });

    it('addNode inserts child node under parent', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree(structuredClone(mockTreeWithChildren));

      usePageBuilderStore.getState().addNode(
        'node-root-1',
        'container-1',
        'PcFieldEmail',
        3,
      );

      const state = usePageBuilderStore.getState();
      const rootNode = state.componentTree[0];
      expect(rootNode.nodes).toHaveLength(3);

      const newChild = rootNode.nodes.find(
        (n) => n.componentName === 'PcFieldEmail',
      );
      expect(newChild).toBeDefined();
      expect(newChild!.componentName).toBe('PcFieldEmail');
      expect(newChild!.weight).toBe(3);
      expect(newChild!.parentId).toBe('node-root-1');
      expect(newChild!.containerId).toBe('container-1');
    });

    it('addNode generates unique UUID for new node', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree([{ ...mockRootNode, nodes: [] }]);

      usePageBuilderStore.getState().addNode(
        null,
        '00000000-0000-0000-0000-000000000000',
        'PcGrid',
        2,
      );

      const state = usePageBuilderStore.getState();
      const newNode = state.componentTree.find(
        (n) => n.componentName === 'PcGrid',
      );
      expect(newNode).toBeDefined();
      // crypto.randomUUID mock counter was reset to 0 in beforeEach,
      // so the first call produces 'mock-uuid-1'
      expect(newNode!.id).toBe('mock-uuid-1');
    });
  });

  // -------------------------------------------------------------------------
  // removeNode Action
  // -------------------------------------------------------------------------

  describe('removeNode action', () => {
    it('removeNode removes a leaf node', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree(structuredClone(mockTreeWithChildren));

      usePageBuilderStore.getState().removeNode('node-child-1');

      const state = usePageBuilderStore.getState();
      const rootNode = state.componentTree[0];
      expect(rootNode.nodes).toHaveLength(1);
      expect(rootNode.nodes[0].id).toBe('node-child-2');
      expect(rootNode.nodes[0].componentName).toBe('PcFieldDate');
      expect(state.isDirty).toBe(true);
    });

    it('removeNode cascade removes node and all descendants', () => {
      // Build a deeply nested tree: root → parent → child → grandchild
      const grandchild: PageBodyNode = {
        id: 'grandchild-1',
        parentId: 'deep-child-1',
        pageId: 'page-uuid-1',
        nodeId: null,
        containerId: 'container-deep',
        weight: 1,
        componentName: 'PcFieldText',
        options: '{}',
        nodes: [],
      };

      const deepChild: PageBodyNode = {
        id: 'deep-child-1',
        parentId: 'parent-node-1',
        pageId: 'page-uuid-1',
        nodeId: null,
        containerId: 'container-mid',
        weight: 1,
        componentName: 'PcSection',
        options: '{}',
        nodes: [grandchild],
      };

      const parentNode: PageBodyNode = {
        id: 'parent-node-1',
        parentId: 'cascade-root',
        pageId: 'page-uuid-1',
        nodeId: null,
        containerId: 'container-1',
        weight: 1,
        componentName: 'PcRow',
        options: '{}',
        nodes: [deepChild],
      };

      const cascadeRoot: PageBodyNode = {
        id: 'cascade-root',
        parentId: null,
        pageId: 'page-uuid-1',
        nodeId: null,
        containerId: '00000000-0000-0000-0000-000000000000',
        weight: 1,
        componentName: 'PcRow',
        options: '{}',
        nodes: [parentNode],
      };

      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree([cascadeRoot]);

      // Remove the parent — should cascade and remove child + grandchild too
      usePageBuilderStore.getState().removeNode('parent-node-1');

      const state = usePageBuilderStore.getState();
      expect(state.componentTree).toHaveLength(1);
      expect(state.componentTree[0].id).toBe('cascade-root');
      expect(state.componentTree[0].nodes).toHaveLength(0);
    });

    it('removeNode removes root node entirely', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree([{ ...mockRootNode, nodes: [] }]);

      usePageBuilderStore.getState().removeNode('node-root-1');

      const state = usePageBuilderStore.getState();
      expect(state.componentTree).toHaveLength(0);
      expect(state.componentTree).toEqual([]);
    });

    it('removeNode clears selectedNodeId if removed node was selected', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree(structuredClone(mockTreeWithChildren));

      // Select a node first
      usePageBuilderStore.getState().selectNode('node-child-1');
      expect(usePageBuilderStore.getState().selectedNodeId).toBe('node-child-1');

      // Remove the selected node
      usePageBuilderStore.getState().removeNode('node-child-1');

      const state = usePageBuilderStore.getState();
      expect(state.selectedNodeId).toBeNull();
      expect(state.selectedNodeOptions).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // moveNode Action
  // -------------------------------------------------------------------------

  describe('moveNode action', () => {
    it('moveNode re-parents a node', () => {
      // Tree: root1 with child1, root2 (empty)
      const root2: PageBodyNode = {
        id: 'node-root-2',
        parentId: null,
        pageId: 'page-uuid-1',
        nodeId: null,
        containerId: '00000000-0000-0000-0000-000000000000',
        weight: 2,
        componentName: 'PcRow',
        options: '{}',
        nodes: [],
      };

      const root1WithChild: PageBodyNode = {
        ...mockRootNode,
        nodes: [{ ...mockChildNode }],
      };

      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree([root1WithChild, root2]);

      // Move child1 from root1 to root2
      usePageBuilderStore.getState().moveNode(
        'node-child-1',
        'node-root-2',
        'container-1',
        1,
      );

      const state = usePageBuilderStore.getState();
      const updatedRoot1 = state.componentTree.find(
        (n) => n.id === 'node-root-1',
      );
      const updatedRoot2 = state.componentTree.find(
        (n) => n.id === 'node-root-2',
      );

      expect(updatedRoot1).toBeDefined();
      expect(updatedRoot1!.nodes).toHaveLength(0);

      expect(updatedRoot2).toBeDefined();
      expect(updatedRoot2!.nodes).toHaveLength(1);
      expect(updatedRoot2!.nodes[0].id).toBe('node-child-1');
      expect(updatedRoot2!.nodes[0].parentId).toBe('node-root-2');
    });

    it('moveNode updates weight', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree(structuredClone(mockTreeWithChildren));

      // Move child2 (weight:2) to weight 0 — should end up before child1 (weight:1)
      usePageBuilderStore.getState().moveNode(
        'node-child-2',
        'node-root-1',
        'container-1',
        0,
      );

      const state = usePageBuilderStore.getState();
      const rootNode = state.componentTree[0];
      const child2 = rootNode.nodes.find((n) => n.id === 'node-child-2');
      const child1 = rootNode.nodes.find((n) => n.id === 'node-child-1');

      expect(child2).toBeDefined();
      expect(child1).toBeDefined();
      // child2's new weight (0) should be less than child1's weight (1)
      expect(child2!.weight).toBeLessThan(child1!.weight);
    });

    it('moveNode marks store as dirty', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree(structuredClone(mockTreeWithChildren));

      expect(usePageBuilderStore.getState().isDirty).toBe(false);

      usePageBuilderStore.getState().moveNode(
        'node-child-1',
        'node-root-1',
        'container-1',
        5,
      );

      expect(usePageBuilderStore.getState().isDirty).toBe(true);
    });
  });

  // -------------------------------------------------------------------------
  // updateNodeOptions Action
  // -------------------------------------------------------------------------

  describe('updateNodeOptions action', () => {
    it('updateNodeOptions updates component options', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      // Tree with one root and one child
      setComponentTree([
        {
          ...mockRootNode,
          nodes: [{ ...mockChildNode }],
        },
      ]);

      const newOptions = {
        label: 'Full Name',
        name: 'full_name',
        required: true,
      };

      usePageBuilderStore.getState().updateNodeOptions(
        'node-child-1',
        newOptions,
      );

      const state = usePageBuilderStore.getState();
      const updatedNode = state.componentTree[0].nodes[0];
      expect(updatedNode.options).toBe(JSON.stringify(newOptions));
      expect(state.isDirty).toBe(true);
    });

    it('updateNodeOptions preserves other nodes unchanged', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree(structuredClone(mockTreeWithChildren));

      const originalChild2Options = mockChildNode2.options;

      usePageBuilderStore.getState().updateNodeOptions(
        'node-child-1',
        { label: 'Updated', name: 'updated_field' },
      );

      const state = usePageBuilderStore.getState();
      const child2 = state.componentTree[0].nodes.find(
        (n) => n.id === 'node-child-2',
      );
      expect(child2).toBeDefined();
      expect(child2!.options).toBe(originalChild2Options);
    });
  });

  // -------------------------------------------------------------------------
  // selectedNodeId State
  // -------------------------------------------------------------------------

  describe('selectedNodeId and ComponentMode state', () => {
    it('selectNode sets selectedNodeId', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree(structuredClone(mockTreeWithChildren));

      usePageBuilderStore.getState().selectNode('node-child-1');

      const state = usePageBuilderStore.getState();
      expect(state.selectedNodeId).toBe('node-child-1');
      // selectNode should also parse the node's options JSON
      expect(state.selectedNodeOptions).toEqual({
        label: 'Name',
        name: 'name',
      });
    });

    it('selectNode with null deselects', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree(structuredClone(mockTreeWithChildren));

      // Select a node first
      usePageBuilderStore.getState().selectNode('node-child-1');
      expect(usePageBuilderStore.getState().selectedNodeId).toBe('node-child-1');

      // Deselect
      usePageBuilderStore.getState().selectNode(null);

      const state = usePageBuilderStore.getState();
      expect(state.selectedNodeId).toBeNull();
      expect(state.selectedNodeOptions).toBeNull();
      expect(state.selectedMode).toBe(ComponentMode.Display);
    });

    it('setSelectedMode updates component mode', () => {
      usePageBuilderStore.getState().setSelectedMode(ComponentMode.Design);
      expect(usePageBuilderStore.getState().selectedMode).toBe(2);
      expect(usePageBuilderStore.getState().selectedMode).toBe(ComponentMode.Design);
    });

    it('setSelectedMode cycles through all modes', () => {
      const { setSelectedMode } = usePageBuilderStore.getState();

      // Display = 1
      setSelectedMode(ComponentMode.Display);
      expect(usePageBuilderStore.getState().selectedMode).toBe(ComponentMode.Display);
      expect(usePageBuilderStore.getState().selectedMode).toBe(1);

      // Design = 2
      setSelectedMode(ComponentMode.Design);
      expect(usePageBuilderStore.getState().selectedMode).toBe(ComponentMode.Design);
      expect(usePageBuilderStore.getState().selectedMode).toBe(2);

      // Options = 3
      setSelectedMode(ComponentMode.Options);
      expect(usePageBuilderStore.getState().selectedMode).toBe(ComponentMode.Options);
      expect(usePageBuilderStore.getState().selectedMode).toBe(3);

      // Help = 4
      setSelectedMode(ComponentMode.Help);
      expect(usePageBuilderStore.getState().selectedMode).toBe(ComponentMode.Help);
      expect(usePageBuilderStore.getState().selectedMode).toBe(4);
    });
  });

  // -------------------------------------------------------------------------
  // Edit Mode
  // -------------------------------------------------------------------------

  describe('edit mode', () => {
    it('initial isEditMode is false', () => {
      expect(usePageBuilderStore.getState().isEditMode).toBe(false);
    });

    it('setEditMode enables edit mode', () => {
      usePageBuilderStore.getState().setEditMode(true);
      expect(usePageBuilderStore.getState().isEditMode).toBe(true);
    });

    it('setEditMode(false) exits edit mode', () => {
      usePageBuilderStore.getState().setEditMode(true);
      expect(usePageBuilderStore.getState().isEditMode).toBe(true);

      usePageBuilderStore.getState().setEditMode(false);
      expect(usePageBuilderStore.getState().isEditMode).toBe(false);
    });
  });

  // -------------------------------------------------------------------------
  // Drag State
  // -------------------------------------------------------------------------

  describe('drag state', () => {
    it('setDragState updates drag properties', () => {
      usePageBuilderStore.getState().setDragState({
        isDragging: true,
        draggedNodeId: 'node-child-1',
      });

      const state = usePageBuilderStore.getState();
      expect(state.dragState.isDragging).toBe(true);
      expect(state.dragState.draggedNodeId).toBe('node-child-1');
      // Unset properties should retain defaults from partial update
      expect(state.dragState.dropTargetNodeId).toBeNull();
      expect(state.dragState.dropPosition).toBeNull();
    });

    it('resetDragState clears all drag properties', () => {
      // First set some drag state
      usePageBuilderStore.getState().setDragState({
        isDragging: true,
        draggedNodeId: 'node-child-1',
        dropTargetNodeId: 'node-root-1',
        dropPosition: 'inside',
      });

      // Verify it was set
      expect(usePageBuilderStore.getState().dragState.isDragging).toBe(true);

      // Reset
      usePageBuilderStore.getState().resetDragState();

      const state = usePageBuilderStore.getState();
      expect(state.dragState.isDragging).toBe(false);
      expect(state.dragState.draggedNodeId).toBeNull();
      expect(state.dragState.dropTargetNodeId).toBeNull();
      expect(state.dragState.dropPosition).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // Component Catalog
  // -------------------------------------------------------------------------

  describe('component catalog', () => {
    it('toggleComponentCatalog toggles open/closed', () => {
      expect(usePageBuilderStore.getState().componentCatalogOpen).toBe(false);

      usePageBuilderStore.getState().toggleComponentCatalog();
      expect(usePageBuilderStore.getState().componentCatalogOpen).toBe(true);

      usePageBuilderStore.getState().toggleComponentCatalog();
      expect(usePageBuilderStore.getState().componentCatalogOpen).toBe(false);
    });
  });

  // -------------------------------------------------------------------------
  // Dirty Tracking
  // -------------------------------------------------------------------------

  describe('dirty tracking', () => {
    it('markClean resets isDirty to false', () => {
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree([{ ...mockRootNode, nodes: [] }]);

      // Add a node to make isDirty true
      usePageBuilderStore.getState().addNode(
        null,
        '00000000-0000-0000-0000-000000000000',
        'PcSection',
        2,
      );
      expect(usePageBuilderStore.getState().isDirty).toBe(true);

      // Mark clean
      usePageBuilderStore.getState().markClean();
      expect(usePageBuilderStore.getState().isDirty).toBe(false);
    });
  });

  // -------------------------------------------------------------------------
  // Reset
  // -------------------------------------------------------------------------

  describe('resetPageBuilder', () => {
    it('resetPageBuilder restores all defaults', () => {
      // Modify multiple state properties to non-default values
      const { setComponentTree } = usePageBuilderStore.getState();
      setComponentTree(structuredClone(mockTreeWithChildren));

      usePageBuilderStore.getState().setEditMode(true);
      usePageBuilderStore.getState().selectNode('node-child-1');
      usePageBuilderStore.getState().setSelectedMode(ComponentMode.Design);
      usePageBuilderStore.getState().setDragState({
        isDragging: true,
        draggedNodeId: 'node-child-1',
      });
      usePageBuilderStore.getState().toggleComponentCatalog();

      // Add a node to make isDirty true
      usePageBuilderStore.getState().addNode(
        'node-root-1',
        'container-1',
        'PcFieldEmail',
        3,
      );

      // Verify state is modified
      expect(usePageBuilderStore.getState().isEditMode).toBe(true);
      expect(usePageBuilderStore.getState().componentTree.length).toBeGreaterThan(0);
      expect(usePageBuilderStore.getState().isDirty).toBe(true);
      expect(usePageBuilderStore.getState().componentCatalogOpen).toBe(true);
      expect(usePageBuilderStore.getState().dragState.isDragging).toBe(true);

      // Reset everything
      usePageBuilderStore.getState().resetPageBuilder();

      const state = usePageBuilderStore.getState();
      expect(state.isEditMode).toBe(false);
      expect(state.componentTree).toEqual([]);
      expect(state.componentTree).toHaveLength(0);
      expect(state.selectedNodeId).toBeNull();
      expect(state.selectedMode).toBe(ComponentMode.Display);
      expect(state.selectedNodeOptions).toBeNull();
      expect(state.dragState.isDragging).toBe(false);
      expect(state.dragState.draggedNodeId).toBeNull();
      expect(state.dragState.dropTargetNodeId).toBeNull();
      expect(state.dragState.dropPosition).toBeNull();
      expect(state.componentCatalogOpen).toBe(false);
      expect(state.isDirty).toBe(false);
    });
  });

  // -------------------------------------------------------------------------
  // State Isolation
  // -------------------------------------------------------------------------

  describe('state isolation', () => {
    it('state does not leak between tests', () => {
      // This test verifies that beforeEach properly resets all state.
      // If any previous test leaked state, these assertions would fail.
      const state = usePageBuilderStore.getState();
      expect(state.isEditMode).toBe(false);
      expect(state.componentTree).toEqual([]);
      expect(state.selectedNodeId).toBeNull();
      expect(state.selectedMode).toBe(ComponentMode.Display);
      expect(state.selectedNodeOptions).toBeNull();
      expect(state.dragState.isDragging).toBe(false);
      expect(state.dragState.draggedNodeId).toBeNull();
      expect(state.dragState.dropTargetNodeId).toBeNull();
      expect(state.dragState.dropPosition).toBeNull();
      expect(state.componentCatalogOpen).toBe(false);
      expect(state.isDirty).toBe(false);
    });
  });
});
