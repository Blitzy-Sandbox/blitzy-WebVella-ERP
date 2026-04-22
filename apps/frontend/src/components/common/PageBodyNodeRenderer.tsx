/**
 * PageBodyNodeRenderer — Dynamic Page Body Node Component Dispatcher
 *
 * Core dispatcher that renders page body nodes by mapping component names
 * to React components. This is the React equivalent of the monolith's
 * `Component.InvokeAsync(rootComponentName, new { context = pcContext })`
 * pattern used across ALL 4 home-type pages (Index, SitePage,
 * ApplicationHome, ApplicationNode).
 *
 * Source pattern (WebVella.Erp.Web/Pages/Index.cshtml lines 15-24):
 *   @foreach (var bodyNode in currentPage.Body) {
 *     var nameArray = bodyNode.ComponentName.Split('.');
 *     var rootComponentName = nameArray[nameArray.Length - 1];
 *     ...
 *     @await Component.InvokeAsync(rootComponentName, new { context = pcContext })
 *   }
 *
 * Each component is loaded via React.lazy() to enable code splitting,
 * keeping per-route chunks below the 200 KB gzipped budget (AAP §0.8.2).
 * Unknown component names render a graceful fallback — they never crash.
 *
 * @module PageBodyNodeRenderer
 */

import React, { lazy, Suspense } from 'react';
import type { PageBodyNode, ComponentMode } from '../../types/page';
import LoadingSpinner from './LoadingSpinner';

/* ─────────────────────────────────────────────────────────────────────────────
 * Props Interface
 * ───────────────────────────────────────────────────────────────────────────── */

/**
 * Props for the PageBodyNodeRenderer component.
 *
 * Defines the contract for rendering a single page body node in the dynamic
 * page builder system. Replaces the C# PageComponentContext from the monolith's
 * WebVella.Erp.Web/Models/ used in ViewComponent dispatch.
 */
export interface PageBodyNodeRendererProps {
  /**
   * Short component name extracted from dotted notation
   * (e.g., "PcRow", "PcSection", "PcFieldText").
   * The caller splits bodyNode.componentName by '.' and takes the last segment.
   */
  componentName: string;

  /**
   * Component-specific configuration options (parsed JSON object from
   * bodyNode.options). Each component interprets its own option keys.
   */
  options: Record<string, unknown>;

  /**
   * Page data model containing datasource results. Passed through to rendered
   * components so they can resolve data-bound expressions.
   */
  dataModel?: Record<string, unknown>;

  /**
   * Rendering mode controlling whether components display in view, design/edit,
   * options, or help mode. Maps to ComponentMode enum values:
   * Display = 1, Design = 2, Options = 3, Help = 4.
   */
  mode: ComponentMode;

  /**
   * The full body node definition including child nodes (bodyNode.nodes).
   * Components like PcRow, PcSection, PcTabNav use this to recursively
   * render their children via nested PageBodyNodeRenderer calls.
   */
  bodyNode?: PageBodyNode;
}

/* ─────────────────────────────────────────────────────────────────────────────
 * Component Registry
 *
 * Maps monolith ViewComponent names (e.g., "PcRow", "PcFieldText") to
 * React.lazy()-loaded components. Every import uses React.lazy() for
 * code splitting, ensuring per-route chunk < 200 KB (AAP §0.8.2).
 *
 * Named exports (DataTable, FilterField) use the .then() wrapper pattern
 * to convert named exports to default exports for React.lazy() compatibility.
 *
 * As new components are added to the codebase, add corresponding entries
 * here. Unknown component names render a graceful fallback — never crash.
 * ───────────────────────────────────────────────────────────────────────────── */

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type LazyPageComponent = React.LazyExoticComponent<React.ComponentType<any>>;

const COMPONENT_REGISTRY: Record<string, LazyPageComponent> = {
  /* ── Layout / Form Structure Components ──────────────────────────────── */
  PcRow: lazy(() => import('../forms/FormRow')),
  PcSection: lazy(() => import('../forms/FormSection')),
  PcForm: lazy(() => import('../forms/DynamicForm')),

  /* ── Container Components ────────────────────────────────────────────── */
  PcTabNav: lazy(() => import('./TabNav')),
  PcModal: lazy(() => import('./Modal')),
  PcDrawer: lazy(() => import('./Drawer')),

  /* ── Widget Components ───────────────────────────────────────────────── */
  PcButton: lazy(() => import('./Button')),
  PcChart: lazy(() => import('./Chart')),
  PcScreenMessage: lazy(() => import('./ScreenMessage')),

  /* ── Page Header ─────────────────────────────────────────────────────── */
  PcPageHeader: lazy(() => import('../layout/Header')),

  /* ── Data Components (named exports — wrapped for React.lazy) ────────── */
  PcGrid: lazy(() =>
    import('../data-table/DataTable').then((m) => ({ default: m.DataTable })),
  ),

  /*
   * Additional data-table and field components should be registered here
   * as their source files are created. Each maps a monolith Pc* name to
   * a React.lazy() import:
   *
   * Data-table (named export → .then() wrapper required):
   *   PcGridFilterField: lazy(() =>
   *     import('../data-table/FilterField').then((m) => ({ default: m.FilterField })),
   *   ),
   *
   * Field components (default exports):
   *   PcFieldText:            lazy(() => import('../fields/TextField')),
   *   PcFieldTextarea:        lazy(() => import('../fields/TextareaField')),
   *   PcFieldNumber:          lazy(() => import('../fields/NumberField')),
   *   PcFieldCurrency:        lazy(() => import('../fields/CurrencyField')),
   *   PcFieldPercent:         lazy(() => import('../fields/PercentField')),
   *   PcFieldDate:            lazy(() => import('../fields/DateField')),
   *   PcFieldDateTime:        lazy(() => import('../fields/DateTimeField')),
   *   PcFieldTime:            lazy(() => import('../fields/TimeField')),
   *   PcFieldEmail:           lazy(() => import('../fields/EmailField')),
   *   PcFieldPhone:           lazy(() => import('../fields/PhoneField')),
   *   PcFieldUrl:             lazy(() => import('../fields/UrlField')),
   *   PcFieldPassword:        lazy(() => import('../fields/PasswordField')),
   *   PcFieldGuid:            lazy(() => import('../fields/GuidField')),
   *   PcFieldCheckbox:        lazy(() => import('../fields/CheckboxField')),
   *   PcFieldCheckboxList:    lazy(() => import('../fields/CheckboxListField')),
   *   PcFieldCheckboxGrid:    lazy(() => import('../fields/CheckboxGridField')),
   *   PcFieldRadioList:       lazy(() => import('../fields/RadioListField')),
   *   PcFieldSelect:          lazy(() => import('../fields/SelectField')),
   *   PcFieldMultiSelect:     lazy(() => import('../fields/MultiSelectField')),
   *   PcFieldFile:            lazy(() => import('../fields/FileField')),
   *   PcFieldMultiFileUpload: lazy(() => import('../fields/MultiFileUploadField')),
   *   PcFieldImage:           lazy(() => import('../fields/ImageField')),
   *   PcFieldHtml:            lazy(() => import('../fields/HtmlField')),
   *   PcFieldCode:            lazy(() => import('../fields/CodeField')),
   *   PcFieldDataCsv:         lazy(() => import('../fields/DataCsvField')),
   *   PcFieldColor:           lazy(() => import('../fields/ColorField')),
   *   PcFieldIcon:            lazy(() => import('../fields/IconField')),
   *   PcFieldHidden:          lazy(() => import('../fields/HiddenField')),
   *   PcFieldAutonumber:      lazy(() => import('../fields/AutonumberField')),
   */
};

/* ─────────────────────────────────────────────────────────────────────────────
 * Component Implementation
 * ───────────────────────────────────────────────────────────────────────────── */

/**
 * Renders a single page body node by resolving its `componentName` against the
 * component registry and displaying the matched component inside a Suspense
 * boundary with a LoadingSpinner fallback.
 *
 * @example Basic usage in a page body loop
 * ```tsx
 * {currentPage.body.map((bodyNode) => {
 *   const name = bodyNode.componentName.split('.').pop() ?? '';
 *   const opts = JSON.parse(bodyNode.options || '{}');
 *   return (
 *     <PageBodyNodeRenderer
 *       key={bodyNode.id}
 *       componentName={name}
 *       options={opts}
 *       dataModel={dataModel}
 *       mode={ComponentMode.Display}
 *       bodyNode={bodyNode}
 *     />
 *   );
 * })}
 * ```
 *
 * @example Recursive child rendering inside a layout component (e.g., PcRow)
 * ```tsx
 * {bodyNode?.nodes?.map((childNode) => {
 *   const childName = childNode.componentName.split('.').pop() ?? '';
 *   const childOptions = JSON.parse(childNode.options || '{}');
 *   return (
 *     <PageBodyNodeRenderer
 *       key={childNode.id}
 *       componentName={childName}
 *       options={childOptions}
 *       dataModel={dataModel}
 *       mode={mode}
 *       bodyNode={childNode}
 *     />
 *   );
 * })}
 * ```
 */
export default function PageBodyNodeRenderer({
  componentName,
  options,
  dataModel,
  mode,
  bodyNode,
}: PageBodyNodeRendererProps): React.ReactElement | null {
  const Component = COMPONENT_REGISTRY[componentName];

  if (!Component) {
    /*
     * Unknown component fallback — renders a visible warning in development
     * and silently returns null in production. This matches the monolith's
     * graceful handling where unknown ViewComponent names would simply not
     * render (Component.InvokeAsync returns empty for unknown names).
     */
    if (import.meta.env.DEV) {
      return (
        <div
          className="my-2 rounded-md border border-yellow-200 bg-yellow-50 p-3"
          role="status"
          aria-label={`Unknown component: ${componentName}`}
        >
          <p className="text-sm text-yellow-700">
            Unknown component:{' '}
            <code className="font-mono text-yellow-800">{componentName}</code>
          </p>
        </div>
      );
    }
    return null;
  }

  return (
    <Suspense fallback={<LoadingSpinner size="sm" />}>
      <Component
        options={options}
        dataModel={dataModel}
        mode={mode}
        bodyNode={bodyNode}
      />
    </Suspense>
  );
}
