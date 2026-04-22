/**
 * Page component TypeScript type definitions for the WebVella ERP frontend.
 *
 * Converted from C# DTOs:
 *   - WebVella.Erp.Web/Models/PageComponentMeta.cs
 *   - WebVella.Erp.Web/Models/PageComponentAttribute.cs
 *   - WebVella.Erp.Web/Models/PageComponentContext.cs
 *
 * All interface property names use camelCase per TypeScript conventions.
 * Where the original JSON property name (from C# [JsonProperty] attributes)
 * differs from the camelCase name, it is documented in the JSDoc comment for
 * API serialisation reference.
 */

import type { PageBodyNode, ComponentMode } from './page';

// ---------------------------------------------------------------------------
// Component Metadata
// ---------------------------------------------------------------------------

/**
 * Metadata describing a registered page-builder component.
 *
 * Stored as JSON in the component registry. Property names map to the
 * `[JsonProperty]` attributes in the source C# class.
 *
 * Source: WebVella.Erp.Web/Models/PageComponentMeta.cs
 */
export interface PageComponentMeta {
  /** Registered component name. JSON key: `name` */
  name: string;

  /** Human-readable display label. JSON key: `label` */
  label: string;

  /** Short description of the component's purpose. JSON key: `description` */
  description: string;

  /** CSS icon class for toolbar display. JSON key: `icon_class` */
  iconClass: string;

  /** Accent colour identifier. JSON key: `color` */
  color: string;

  /** Grouping category for the component palette. JSON key: `category` */
  category: string;

  /** Component library owner. JSON key: `library` */
  library: string;

  /** URL for the design-mode view. JSON key: `design_view_url` */
  designViewUrl: string;

  /** URL for the options / configuration view. JSON key: `options_view_url` */
  optionsViewUrl: string;

  /** URL for the inline help view. JSON key: `help_view_url` */
  helpViewUrl: string;

  /** URL for the client-side service JavaScript. JSON key: `service_js_url` */
  serviceJsUrl: string;

  /** Semantic version string (default `"1.0.0"`). JSON key: `version` */
  version: string;

  /** Whether the component renders inline rather than as a block. JSON key: `is_inline` */
  isInline: boolean;

  /** Number of times this component has been placed on pages. JSON key: `usage_counter` */
  usageCounter: number;

  /**
   * ISO 8601 timestamp of the last time this component was placed on a page.
   * Maps from C# `DateTime` — serialised as an ISO date string.
   * JSON key: `last_used_on`
   */
  lastUsedOn: string;
}

// ---------------------------------------------------------------------------
// Component Attribute
// ---------------------------------------------------------------------------

/**
 * Declarative attribute metadata attached to a page-builder component class.
 *
 * In the monolith this was a C# `Attribute` subclass applied to ViewComponent
 * classes via `[PageComponentAttribute]`. In the React SPA it serves as a
 * static descriptor for each registered component, used for palette display
 * and component discovery.
 *
 * Source: WebVella.Erp.Web/Models/PageComponentAttribute.cs
 */
export interface PageComponentAttribute {
  /** Human-readable display label. JSON key: `label` */
  label: string;

  /** Short description of the component's purpose. JSON key: `description` */
  description: string;

  /** CSS icon class for toolbar display. JSON key: `icon_class` */
  iconClass: string;

  /** Accent colour identifier. JSON key: `color` */
  color: string;

  /** Grouping category for the component palette. JSON key: `category` */
  category: string;

  /** Component library owner. JSON key: `library` */
  library: string;

  /** Semantic version string. JSON key: `version` */
  version: string;

  /** Whether the component renders inline rather than as a block. JSON key: `is_inline` */
  isInline: boolean;

  /** Searchable tags for component discovery and filtering. JSON key: `tags` */
  tags: string[];
}

// ---------------------------------------------------------------------------
// Component Context
// ---------------------------------------------------------------------------

/**
 * Runtime context passed to a page-builder component when rendering.
 *
 * Contains the component's node position in the body tree, its resolved
 * options, the current rendering mode, and a data-model dictionary
 * populated by page-level data sources.
 *
 * Source: WebVella.Erp.Web/Models/PageComponentContext.cs
 */
export interface PageComponentContext {
  /**
   * Arbitrary key/value dictionary shared across components during a
   * single render pass.
   *
   * Maps from C# `IDictionary<object, object>`.
   */
  items: Record<string, unknown>;

  /** The component's node in the hierarchical page body tree. */
  node: PageBodyNode;

  /**
   * Resolved component configuration options.
   * `null` when no options are set for this component instance.
   *
   * Maps from C# `JObject`.
   */
  options: Record<string, unknown> | null;

  /**
   * Current rendering mode — determines whether the component renders
   * in Display, Design, Options, or Help mode.
   */
  mode: ComponentMode;

  /**
   * Page-level data model populated from bound data sources.
   * `null` when no data sources are configured for the page.
   *
   * Maps from C# `PageDataModel` — a complex dictionary-of-dictionaries
   * structure simplified to a generic record for frontend consumption.
   */
  dataModel: Record<string, unknown> | null;
}
