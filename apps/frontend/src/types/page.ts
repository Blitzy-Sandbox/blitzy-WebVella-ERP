/**
 * Page-related TypeScript type definitions for the WebVella ERP frontend.
 *
 * Converted from C# DTOs:
 *   - WebVella.Erp.Web/Models/PageType.cs
 *   - WebVella.Erp.Web/Models/ComponentMode.cs
 *   - WebVella.Erp.Web/Models/TabNavRenderType.cs
 *   - WebVella.Erp.Web/Models/PageUtilsActionType.cs
 *   - WebVella.Erp.Web/Models/PageBodyNode.cs
 *   - WebVella.Erp.Web/Models/PageDataSource.cs
 *   - WebVella.Erp.Web/Models/ErpPage.cs
 *   - WebVella.Erp.Web/Models/PageSwitchItem.cs
 *   - WebVella.Erp/Api/Models/DataSourceParameter.cs
 */

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/**
 * Describes the type of an ERP page.
 *
 * Numeric values match the original C# enum so that API payloads are
 * directly interchangeable.
 *
 * Source: WebVella.Erp.Web/Models/PageType.cs
 */
export enum PageType {
  /** Landing / home page */
  Home = 0,
  /** Standalone site-level page */
  Site = 1,
  /** Application-scoped page */
  Application = 2,
  /** Record list view */
  RecordList = 3,
  /** Record creation form */
  RecordCreate = 4,
  /** Record details view */
  RecordDetails = 5,
  /** Record management / edit form */
  RecordManage = 6,
}

/**
 * Rendering mode for a page-builder component.
 *
 * Source: WebVella.Erp.Web/Models/ComponentMode.cs
 */
export enum ComponentMode {
  /** Read-only display */
  Display = 1,
  /** Drag-and-drop design canvas */
  Design = 2,
  /** Component configuration panel */
  Options = 3,
  /** Inline help overlay */
  Help = 4,
}

/**
 * Visual style for tab-navigation components.
 *
 * Source: WebVella.Erp.Web/Models/TabNavRenderType.cs
 */
export enum TabNavRenderType {
  /** Standard tab style */
  TABS = 1,
  /** Pill / rounded style */
  PILLS = 2,
}

/**
 * Action type for page-level utility buttons (e.g. form toolbar).
 *
 * Source: WebVella.Erp.Web/Models/PageUtilsActionType.cs
 */
export enum PageUtilsActionType {
  /** Cancel and revert changes */
  Cancel = 0,
  /** Submit the enclosing form */
  SubmitForm = 1,
  /** Button is visible but non-interactive */
  Disabled = 2,
  /** Show confirmation dialog then submit the form */
  ConfirmAndSubmitForm = 3,
}

// ---------------------------------------------------------------------------
// Supporting Interfaces
// ---------------------------------------------------------------------------

/**
 * A single translatable label resource entry.
 *
 * Defined inline to avoid circular imports with `app.ts` which also
 * references this shape.
 */
export interface TranslationResource {
  /** BCP-47 locale code (e.g. "en-US", "bg-BG") */
  locale: string;
  /** Translation lookup key */
  key: string;
  /** Translated text value */
  value: string;
}

/**
 * Parameter definition for a page-level data source binding.
 *
 * Source: WebVella.Erp/Api/Models/DataSourceParameter.cs
 */
export interface DataSourceParameter {
  /** Parameter name used in the data-source query or code */
  name: string;
  /** Data-type descriptor (e.g. "text", "guid", "int") */
  type: string;
  /** Default or bound value expression */
  value: string;
  /** When true, silently ignore type-conversion failures */
  ignoreParseErrors: boolean;
}

// ---------------------------------------------------------------------------
// Page Model Interfaces
// ---------------------------------------------------------------------------

/**
 * A single node in the hierarchical page body tree.
 *
 * The `nodes` property is recursive — each node may contain child nodes
 * to an arbitrary depth, forming the page-builder layout tree.
 *
 * Source: WebVella.Erp.Web/Models/PageBodyNode.cs
 */
export interface PageBodyNode {
  /** Unique identifier for this body node */
  id: string;
  /** Parent node identifier; `null` for root-level nodes */
  parentId: string | null;
  /** Owning page identifier */
  pageId: string;
  /** Optional embedded layout node identifier */
  nodeId: string | null;
  /** Container slot identifier within the parent component */
  containerId: string;
  /** Sort weight within the container (lower values render first) */
  weight: number;
  /** Registered component name (e.g. "PcFieldText", "PcRow") */
  componentName: string;
  /** JSON-serialised component configuration chosen by the user */
  options: string;
  /** Ordered child nodes forming the recursive layout tree */
  nodes: PageBodyNode[];
}

/**
 * Binds a named data source to a page, along with parameter overrides.
 *
 * Source: WebVella.Erp.Web/Models/PageDataSource.cs
 */
export interface PageDataSource {
  /** Unique identifier for this page–data-source binding */
  id: string;
  /** Owning page identifier */
  pageId: string;
  /** Reference to the underlying data-source definition */
  dataSourceId: string;
  /** Display / reference name within the page */
  name: string;
  /** Parameter overrides for the data source execution */
  parameters: DataSourceParameter[];
}

/**
 * Full definition of an ERP page including metadata, type, layout, and body tree.
 *
 * Source: WebVella.Erp.Web/Models/ErpPage.cs
 */
export interface ErpPage {
  /** Unique page identifier */
  id: string;
  /** Sort weight for ordering sibling pages (default 10) */
  weight: number;
  /** Human-readable label */
  label: string;
  /** Locale-specific label overrides */
  labelTranslations: TranslationResource[];
  /** URL-safe page name / slug */
  name: string;
  /** CSS icon class override; `null` uses the default icon */
  iconClass: string | null;
  /** System pages cannot be deleted or hidden from listings */
  system: boolean;
  /** Discriminator for the page's functional role */
  type: PageType;
  /** Associated application identifier (required for Application pages) */
  appId: string | null;
  /** Associated entity identifier (required for record-level pages) */
  entityId: string | null;
  /** Sitemap area identifier for sibling-page scoping */
  areaId: string | null;
  /** Sitemap node identifier for sibling-page scoping */
  nodeId: string | null;
  /** When true the body is raw Razor source rather than a node tree */
  isRazorBody: boolean;
  /** Raw Razor view source (empty when `isRazorBody` is false) */
  razorBody: string;
  /** Layout template identifier */
  layout: string;
  /** Hierarchical page-builder body node tree */
  body: PageBodyNode[];
}

/**
 * Lightweight item for page-switch navigation controls (e.g. tabs that
 * switch between related pages).
 *
 * Source: WebVella.Erp.Web/Models/PageSwitchItem.cs
 */
export interface PageSwitchItem {
  /** Visible label text */
  label: string;
  /** Navigation target URL */
  url: string;
  /** Whether this item represents the currently active page */
  isSelected: boolean;
}
