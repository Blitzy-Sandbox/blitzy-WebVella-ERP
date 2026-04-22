/**
 * App, Sitemap, Navigation, and Menu TypeScript interfaces.
 *
 * Converted from C# DTOs in WebVella.Erp.Web/Models/:
 *   - TranslationResource.cs  → TranslationResource
 *   - SitemapNodeType.cs      → SitemapNodeType (const enum)
 *   - SitemapNode.cs          → SitemapNode
 *   - SitemapGroup.cs         → SitemapGroup
 *   - SitemapArea.cs          → SitemapArea
 *   - Sitemap.cs              → Sitemap
 *   - AppEntity.cs            → AppEntity
 *   - App.cs                  → App
 *   - MenuItem.cs             → MenuItem
 *
 * Naming conventions:
 *   - C# PascalCase properties → camelCase TypeScript properties
 *   - C# snake_case JSON keys (via [JsonProperty]) → camelCase TS props
 *   - C# Guid / Guid?   → string / (string | null)
 *   - C# List<Guid>     → string[]
 *   - C# List<T>        → T[]
 *   - C# int            → number
 *   - C# bool           → boolean
 */

import type { Entity } from './entity';
import type { ErpPage } from './page';

// ---------------------------------------------------------------------------
// Translation Resource
// ---------------------------------------------------------------------------

/**
 * A single translatable text entry used throughout the sitemap tree
 * (area labels, group labels, node labels).
 *
 * Source: WebVella.Erp.Web/Models/TranslationResource.cs
 *
 * JSON keys: locale, key, value — all lower-case in the C# model and
 * therefore identical in camelCase.
 */
export interface TranslationResource {
  /** BCP-47 locale identifier (e.g. "en-US", "bg-BG"). */
  locale: string;
  /** Translation lookup key (e.g. "sitemapId-areaName-title"). */
  key: string;
  /** Translated text value for the given locale/key combination. */
  value: string;
}

// ---------------------------------------------------------------------------
// Sitemap Node Type Enum
// ---------------------------------------------------------------------------

/**
 * Discriminator for the type of content a sitemap node represents.
 *
 * Mirrors C# `SitemapNodeType` enum from SitemapNodeType.cs.
 * Numeric values match the C# backing integers exactly to ensure JSON
 * round-trip compatibility.
 *
 *   EntityList       = 1  — linked to an entity's record list view
 *   ApplicationPage  = 2  — standalone application page
 *   Url              = 3  — external or hard-coded URL
 */
export const enum SitemapNodeType {
  EntityList = 1,
  ApplicationPage = 2,
  Url = 3,
}

// ---------------------------------------------------------------------------
// Sitemap Node
// ---------------------------------------------------------------------------

/**
 * A single navigation entry within a sitemap area.
 *
 * Nodes can be entity-driven (linked to entity pages via `entityId` and
 * `entityListPages` / `entityCreatePages` / `entityDetailsPages` /
 * `entityManagePages` arrays) or static URL links.
 *
 * Source: WebVella.Erp.Web/Models/SitemapNode.cs
 */
export interface SitemapNode {
  /** Unique node identifier (GUID as string; C# default: Guid.Empty). */
  id: string;

  /**
   * Parent node identifier for hierarchical nesting.
   * `null` when this node is a root-level entry.
   * (C# Guid?, JSON key: parent_id)
   */
  parentId?: string | null;

  /** Display sort weight within the containing group (default 1). */
  weight: number;

  /**
   * Name of the group this node belongs to.
   * Empty string means the node has no group.
   * (JSON key: group_name)
   */
  groupName: string;

  /** Human-readable label displayed in navigation menus. */
  label: string;

  /** Machine-readable URL-safe name / slug. */
  name: string;

  /** CSS icon class for the navigation icon (JSON key: icon_class). */
  iconClass: string;

  /** Hard-coded URL for Url-type nodes (JSON key: url). */
  url: string;

  /**
   * Locale-specific label overrides.
   * (JSON key: label_translations)
   */
  labelTranslations: TranslationResource[];

  /**
   * Role IDs (GUIDs) granted access to this node.
   * Empty array means visible to all roles.
   */
  access: string[];

  /** Node content type (entity list, application page, or URL). */
  type: SitemapNodeType;

  /**
   * Entity identifier when the node is entity-driven.
   * `null` for non-entity node types.
   * (C# Guid?, JSON key: entity_id)
   */
  entityId?: string | null;

  /**
   * Page IDs for the entity's record list views.
   * (JSON key: entity_list_pages)
   */
  entityListPages: string[];

  /**
   * Page IDs for the entity's record creation forms.
   * (JSON key: entity_create_pages)
   */
  entityCreatePages: string[];

  /**
   * Page IDs for the entity's record detail views.
   * (JSON key: entity_details_pages)
   */
  entityDetailsPages: string[];

  /**
   * Page IDs for the entity's record management/edit forms.
   * (JSON key: entity_manage_pages)
   */
  entityManagePages: string[];
}

// ---------------------------------------------------------------------------
// Sitemap Group
// ---------------------------------------------------------------------------

/**
 * A logical group within a sitemap area used to organise related nodes
 * under a shared heading.
 *
 * Source: WebVella.Erp.Web/Models/SitemapGroup.cs
 */
export interface SitemapGroup {
  /** Unique group identifier (GUID as string). */
  id: string;

  /** Display sort weight within the area (default 1). */
  weight: number;

  /** Human-readable group heading label. */
  label: string;

  /**
   * Machine-readable identifier referenced by SitemapNode.groupName.
   * (JSON key: name)
   */
  name: string;

  /**
   * Locale-specific label overrides.
   * (JSON key: label_translations)
   */
  labelTranslations: TranslationResource[];

  /**
   * Role IDs (GUIDs) for which this group is rendered.
   * Empty array means rendered for all roles.
   * (JSON key: render_roles)
   */
  renderRoles: string[];
}

// ---------------------------------------------------------------------------
// Sitemap Area
// ---------------------------------------------------------------------------

/**
 * A top-level section of the application's sitemap, containing groups
 * and navigation nodes.
 *
 * Source: WebVella.Erp.Web/Models/SitemapArea.cs
 */
export interface SitemapArea {
  /** Unique area identifier (GUID as string). */
  id: string;

  /** Owning application identifier (JSON key: app_id). */
  appId: string;

  /** Display sort weight among sibling areas (default 1). */
  weight: number;

  /** Human-readable area label. */
  label: string;

  /** Longer description text for the area. */
  description: string;

  /** Machine-readable URL-safe name / slug. */
  name: string;

  /** CSS icon class for the area icon (JSON key: icon_class). */
  iconClass: string;

  /**
   * Whether group headings are displayed within this area.
   * (JSON key: show_group_names; C# default: false)
   */
  showGroupNames: boolean;

  /** Accent colour for the area (CSS colour value). */
  color: string;

  /**
   * Locale-specific label overrides.
   * (JSON key: label_translations)
   */
  labelTranslations: TranslationResource[];

  /**
   * Locale-specific description overrides.
   * (JSON key: description_translations)
   */
  descriptionTranslations: TranslationResource[];

  /** Ordered list of logical groups within this area. */
  groups: SitemapGroup[];

  /** Ordered list of navigation nodes within this area. */
  nodes: SitemapNode[];

  /**
   * Role IDs (GUIDs) granted access to this area.
   * Empty array means visible to all roles.
   */
  access: string[];
}

// ---------------------------------------------------------------------------
// Sitemap
// ---------------------------------------------------------------------------

/**
 * Root container for the application navigation sitemap.
 *
 * Source: WebVella.Erp.Web/Models/Sitemap.cs
 */
export interface Sitemap {
  /** Ordered list of top-level sitemap areas. */
  areas: SitemapArea[];
}

// ---------------------------------------------------------------------------
// App Entity
// ---------------------------------------------------------------------------

/**
 * Associates an entity definition with an application, along with the
 * entity pages selected for display within that application context.
 *
 * Source: WebVella.Erp.Web/Models/AppEntity.cs
 *
 * If `selectedPages` is empty it means all entity pages are selected and
 * the default page is presented. If the array has entries, the first item
 * is the default page to present.
 */
export interface AppEntity {
  /**
   * The entity definition (id, name, label, fields, etc.) associated
   * with this application.
   */
  entity: Entity;

  /**
   * Entity-specific pages selected for this application context.
   * (JSON key: selected_pages)
   */
  selectedPages: ErpPage[];
}

// ---------------------------------------------------------------------------
// App
// ---------------------------------------------------------------------------

/**
 * Top-level application definition containing metadata, navigation
 * sitemap, home pages, and associated entities.
 *
 * Source: WebVella.Erp.Web/Models/App.cs
 */
export interface App {
  /** Unique application identifier (GUID as string; C# default: Guid.Empty). */
  id: string;

  /** Machine-readable application name. */
  name: string;

  /** Human-readable application label. */
  label: string;

  /** Longer description text for the application. */
  description: string;

  /** CSS icon class for the application icon (JSON key: icon_class). */
  iconClass: string;

  /** Application author / owner name. */
  author: string;

  /** Accent colour for the application (CSS colour value; C# default: "#2196F3"). */
  color: string;

  /**
   * Navigation sitemap tree.
   * `null` when no sitemap has been configured.
   */
  sitemap: Sitemap | null;

  /**
   * Home/landing pages for the application.
   * (JSON key: home_pages)
   */
  homePages: ErpPage[];

  /** Entities associated with this application. */
  entities: AppEntity[];

  /** Display sort weight among sibling applications (default 1). */
  weight: number;

  /**
   * Role IDs (GUIDs) granted access (shown in menu).
   * Empty array means visible to all roles.
   */
  access: string[];
}

// ---------------------------------------------------------------------------
// Menu Item
// ---------------------------------------------------------------------------

/**
 * A node in the hierarchical navigation menu tree.
 *
 * The `nodes` property is recursive — each menu item may contain child
 * items forming nested dropdown or flyout menus.
 *
 * Source: WebVella.Erp.Web/Models/MenuItem.cs
 */
export interface MenuItem {
  /** Unique menu item identifier (GUID as string). */
  id: string;

  /**
   * Parent menu item identifier for tree nesting.
   * `null` for root-level items.
   * (C# Guid?, JSON key: parent_id)
   */
  parentId?: string | null;

  /** Rendered content (HTML or plain text depending on `isHtml`). */
  content: string;

  /**
   * Additional CSS class(es) to apply (e.g. "active").
   * JSON key is literally "class" per the C# [JsonProperty("class")].
   */
  class: string;

  /**
   * Whether `content` contains raw HTML to be rendered unescaped.
   * (JSON key: is_html; C# default: true)
   */
  isHtml: boolean;

  /**
   * Whether to render the standard list-item wrapper around this item.
   * (JSON key: render_wrapper; C# default: true)
   */
  renderWrapper: boolean;

  /**
   * Child menu items forming nested sub-menus.
   * (Recursive self-reference; JSON key: nodes)
   */
  nodes: MenuItem[];

  /**
   * Whether the dropdown/flyout for this item opens to the right.
   * (JSON key: is_dropdown_right; C# default: false)
   */
  isDropdownRight: boolean;

  /**
   * Sort order within the parent container (default 10).
   * (JSON key: sort_order)
   */
  sortOrder: number;
}
