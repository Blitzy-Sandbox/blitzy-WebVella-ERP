/**
 * Plugin Detail View Page
 *
 * Read-only detail view for a single registered plugin, displaying all 13+
 * metadata properties from the monolith's `ErpPlugin.cs`, plugin-specific
 * configuration data (from `plugin_data`), and version/patch information.
 *
 * Replaces:
 *  - `IErpService.Plugins` list inspection
 *  - `ErpPlugin` metadata display from SDK admin pages
 *  - `ErpPlugin.GetPluginData()` configuration retrieval
 *
 * Route: /plugins/:pluginId (lazy-loaded via React.lazy())
 *
 * Data fetching:
 *  - `usePlugin(pluginId)` — single plugin metadata
 *  - `usePluginData(plugin.name)` — plugin configuration JSON
 *  - `useDeletePlugin()` — mutation for plugin removal
 *
 * AAP compliance:
 *  - §0.8.1 — Full behavioral parity: all ErpPlugin properties displayed
 *  - §0.8.2 — Per-route chunk < 200KB gzipped
 *  - Pure static SPA — no SSR, no server components
 *  - Tailwind CSS — no Bootstrap classes
 *  - TanStack Query — all data fetching via hooks
 *
 * @module pages/plugins/PluginDetails
 */

import { useState } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import {
  usePlugin,
  usePluginData,
  useDeletePlugin,
} from '../../hooks/usePlugins';
import type { Plugin } from '../../hooks/usePlugins';
import Modal from '../../components/common/Modal';

/* ------------------------------------------------------------------ */
/*  Helpers                                                            */
/* ------------------------------------------------------------------ */

/**
 * Formats a version string. The legacy monolith used YYYYMMDD integers
 * (e.g. "20210429") as patch-level versions. When that pattern is detected
 * the value is displayed as a locale-formatted date; otherwise "v{version}".
 */
function formatVersion(version: string): string {
  if (/^\d{8}$/.test(version)) {
    const year = Number(version.slice(0, 4));
    const month = Number(version.slice(4, 6)) - 1;
    const day = Number(version.slice(6, 8));
    const date = new Date(year, month, day);
    if (!Number.isNaN(date.getTime()) && date.getMonth() === month) {
      return date.toLocaleDateString(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
      });
    }
  }
  return version ? `v${version}` : '\u2014';
}

/**
 * Attempts to pretty-print a JSON string. Returns the formatted string
 * or the original value if parsing fails.
 */
function prettyPrintJson(jsonString: string): string {
  try {
    const parsed: unknown = JSON.parse(jsonString);
    return JSON.stringify(parsed, null, 2);
  } catch {
    return jsonString;
  }
}

/**
 * Extracts a patch version from plugin data JSON if it follows the
 * `PluginSettings` model pattern (contains a `version` field).
 */
function extractPatchVersion(jsonString: string | undefined | null): string | null {
  if (!jsonString) return null;
  try {
    const parsed = JSON.parse(jsonString) as Record<string, unknown>;
    if (typeof parsed.version === 'number' || typeof parsed.version === 'string') {
      return String(parsed.version);
    }
  } catch {
    /* invalid JSON — no patch version available */
  }
  return null;
}

/* ------------------------------------------------------------------ */
/*  Inline SVG Icons (currentColor, sized via className)              */
/* ------------------------------------------------------------------ */

/** Cube/puzzle-piece fallback icon for plugins without an iconUrl. */
function PluginIcon({ className = 'w-12 h-12' }: { className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M20 7l-8-4-8 4m16 0l-8 4m8-4v10l-8 4M4 7l8 4M4 7v10l8 4m0-10v10" />
    </svg>
  );
}

/** Left-arrow icon for the "Back" action. */
function ArrowLeftIcon({ className = 'w-5 h-5' }: { className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M19 12H5m0 0l7 7m-7-7l7-7" />
    </svg>
  );
}

/** Pencil/edit icon for the "Manage" button. */
function PencilIcon({ className = 'w-5 h-5' }: { className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
    </svg>
  );
}

/** Trash icon for the "Delete" action. */
function TrashIcon({ className = 'w-5 h-5' }: { className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
    </svg>
  );
}

/** External link icon appended to outbound links. */
function ExternalLinkIcon({ className = 'w-4 h-4' }: { className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M18 13v6a2 2 0 01-2 2H5a2 2 0 01-2-2V8a2 2 0 012-2h6m4-3h6v6m-11 5L21 3" />
    </svg>
  );
}

/** Warning triangle icon for error states. */
function WarningIcon({ className = 'w-6 h-6' }: { className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M12 9v2m0 4h.01M10.29 3.86l-8.6 14.94A2 2 0 003.41 22h17.18a2 2 0 001.72-3.2l-8.6-14.94a2 2 0 00-3.42 0z" />
    </svg>
  );
}

/* ------------------------------------------------------------------ */
/*  Sub-components                                                     */
/* ------------------------------------------------------------------ */

/**
 * A definition-list row used to display a single plugin metadata property.
 * Uses `dt/dd` semantics for label/value pairs within a `dl` container.
 */
function DetailRow({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="grid grid-cols-1 gap-1 py-3 sm:grid-cols-3 sm:gap-4">
      <dt className="text-sm font-medium text-gray-500">{label}</dt>
      <dd className="text-sm text-gray-900 sm:col-span-2">{children}</dd>
    </div>
  );
}

/**
 * Renders a link that opens in a new tab with appropriate security attributes.
 * Displays the URL text alongside an external-link indicator icon.
 * Returns a plain em-dash when the URL is empty or undefined.
 */
function ExternalLink({ href, label }: { href: string | undefined | null; label?: string }) {
  if (!href) {
    return <span className="text-gray-400">{'\u2014'}</span>;
  }
  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className="inline-flex items-center gap-1 text-indigo-600 hover:text-indigo-900 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
    >
      {label ?? href}
      <ExternalLinkIcon className="w-3.5 h-3.5 shrink-0" />
    </a>
  );
}

/* ------------------------------------------------------------------ */
/*  Loading Skeleton                                                   */
/* ------------------------------------------------------------------ */

/** Animated loading skeleton shown while plugin data is being fetched. */
function LoadingSkeleton() {
  return (
    <div className="space-y-6 animate-pulse" role="status" aria-label="Loading plugin details">
      {/* Breadcrumb skeleton */}
      <div className="h-4 w-48 rounded bg-gray-200" />

      {/* Header area */}
      <div className="flex items-start gap-4">
        <div className="h-12 w-12 rounded-lg bg-gray-200" />
        <div className="flex-1 space-y-2">
          <div className="h-6 w-40 rounded bg-gray-200" />
          <div className="h-4 w-24 rounded bg-gray-200" />
        </div>
      </div>

      {/* Metadata card */}
      <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <div className="space-y-4">
          {Array.from({ length: 8 }).map((_, i) => (
            <div key={i} className="grid grid-cols-3 gap-4">
              <div className="h-4 w-24 rounded bg-gray-200" />
              <div className="col-span-2 h-4 w-full rounded bg-gray-200" />
            </div>
          ))}
        </div>
      </div>

      {/* Settings card */}
      <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <div className="h-5 w-32 rounded bg-gray-200 mb-4" />
        <div className="h-24 w-full rounded bg-gray-200" />
      </div>

      <span className="sr-only">Loading…</span>
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  Main Component                                                     */
/* ------------------------------------------------------------------ */

/**
 * Plugin detail view page.
 *
 * Displays all 13+ metadata properties from `ErpPlugin.cs`, plugin
 * configuration data from `plugin_data`, version/patch information,
 * and action buttons for managing or deleting the plugin.
 *
 * Route: /plugins/:pluginId (lazy-loaded, admin-protected)
 */
function PluginDetails() {
  const { pluginId } = useParams<{ pluginId: string }>();
  const navigate = useNavigate();

  /* ---- Data hooks ------------------------------------------------ */
  const {
    data: pluginResponse,
    isLoading: isPluginLoading,
    isError: isPluginError,
    error: pluginError,
  } = usePlugin(pluginId ?? '');

  const plugin: Plugin | undefined = pluginResponse?.object;

  const {
    data: pluginDataResponse,
    isLoading: isPluginDataLoading,
  } = usePluginData(plugin?.name ?? '');

  const pluginDataRaw: string | undefined = pluginDataResponse?.object ?? undefined;

  const deleteMutation = useDeletePlugin();

  /* ---- Local UI state -------------------------------------------- */
  const [showDeleteModal, setShowDeleteModal] = useState(false);

  /* ---- Derived values -------------------------------------------- */
  const patchVersion = extractPatchVersion(pluginDataRaw ?? null);

  /* ---- Handlers -------------------------------------------------- */

  /** Opens the delete confirmation modal. */
  function handleDeleteClick() {
    deleteMutation.reset();
    setShowDeleteModal(true);
  }

  /** Executes the deletion after user confirms in the modal. */
  function handleDeleteConfirm() {
    if (!plugin) return;

    deleteMutation.mutate(plugin.id, {
      onSuccess: () => {
        setShowDeleteModal(false);
        navigate('/plugins');
      },
    });
  }

  /** Cancels the delete action and hides the modal. */
  function handleDeleteCancel() {
    setShowDeleteModal(false);
  }

  /* ---- Loading state --------------------------------------------- */
  if (isPluginLoading) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <LoadingSkeleton />
      </div>
    );
  }

  /* ---- Error state ----------------------------------------------- */
  if (isPluginError || !plugin) {
    const errorMessage =
      pluginError instanceof Error
        ? pluginError.message
        : 'The requested plugin could not be found.';

    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        {/* Breadcrumb */}
        <nav aria-label="Breadcrumb" className="mb-6">
          <ol className="flex items-center gap-2 text-sm text-gray-500">
            <li>
              <Link to="/" className="hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500">
                Home
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li>
              <Link to="/plugins" className="hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500">
                Plugins
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li className="text-gray-900 font-medium" aria-current="page">
              Not Found
            </li>
          </ol>
        </nav>

        {/* Error card */}
        <div className="rounded-lg border border-red-200 bg-red-50 p-8 text-center">
          <WarningIcon className="mx-auto mb-4 w-12 h-12 text-red-400" />
          <h1 className="text-xl font-semibold text-gray-900 mb-2">
            Plugin Not Found
          </h1>
          <p className="text-sm text-gray-600 mb-6">{errorMessage}</p>
          <Link
            to="/plugins"
            className="inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            <ArrowLeftIcon className="w-4 h-4" />
            Back to Plugins
          </Link>
        </div>
      </div>
    );
  }

  /* ---- Render: plugin detail view -------------------------------- */
  return (
    <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
      {/* Breadcrumb */}
      <nav aria-label="Breadcrumb" className="mb-6">
        <ol className="flex items-center gap-2 text-sm text-gray-500">
          <li>
            <Link to="/" className="hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500">
              Home
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li>
            <Link to="/plugins" className="hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500">
              Plugins
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li className="text-gray-900 font-medium" aria-current="page">
            {plugin.name || 'Plugin Details'}
          </li>
        </ol>
      </nav>

      {/* Page header with icon, name, version badge, and actions */}
      <div className="mb-8 flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
        {/* Left: icon + title cluster */}
        <div className="flex items-start gap-4">
          {/* Plugin icon / fallback */}
          <div className="flex h-14 w-14 shrink-0 items-center justify-center rounded-lg bg-indigo-50 text-indigo-500">
            {plugin.iconUrl ? (
              <img
                src={plugin.iconUrl}
                alt={`${plugin.name} icon`}
                width={40}
                height={40}
                className="h-10 w-10 rounded object-cover bg-gray-200"
                loading="lazy"
                decoding="async"
              />
            ) : (
              <PluginIcon className="w-8 h-8" />
            )}
          </div>

          <div>
            <h1 className="text-2xl font-bold text-gray-900">{plugin.name || '\u2014'}</h1>
            <div className="mt-1 flex flex-wrap items-center gap-2">
              {/* Version badge */}
              <span className="inline-flex items-center rounded-full bg-indigo-100 px-2.5 py-0.5 text-xs font-medium text-indigo-800">
                {formatVersion(plugin.version)}
              </span>

              {/* Status badge — active is inferred from plugin existence */}
              <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">
                Active
              </span>

              {/* Patch version from plugin data, if available */}
              {patchVersion && patchVersion !== plugin.version && (
                <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
                  Patch: {formatVersion(patchVersion)}
                </span>
              )}
            </div>
          </div>
        </div>

        {/* Right: action buttons */}
        <div className="flex items-center gap-3">
          <Link
            to="/plugins"
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
          >
            <ArrowLeftIcon className="w-4 h-4" />
            Back to Plugins
          </Link>
          <Link
            to={`/plugins/${pluginId}/manage`}
            className="inline-flex items-center gap-1.5 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            <PencilIcon className="w-4 h-4" />
            Edit / Manage
          </Link>
          <button
            type="button"
            onClick={handleDeleteClick}
            className="inline-flex items-center gap-1.5 rounded-md border border-red-300 bg-white px-4 py-2 text-sm font-medium text-red-700 shadow-sm hover:bg-red-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500"
          >
            <TrashIcon className="w-4 h-4" />
            Delete
          </button>
        </div>
      </div>

      {/* Plugin Metadata Card */}
      <section aria-labelledby="metadata-heading" className="mb-6">
        <div className="rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 id="metadata-heading" className="text-lg font-semibold text-gray-900">
              Plugin Information
            </h2>
          </div>
          <div className="px-6">
            <dl className="divide-y divide-gray-100">
              {/* Name */}
              <DetailRow label="Name">
                <span className="font-semibold">{plugin.name || '\u2014'}</span>
              </DetailRow>

              {/* Description */}
              <DetailRow label="Description">
                {plugin.description ? (
                  <p className="whitespace-pre-wrap overflow-wrap-break-word">
                    {plugin.description}
                  </p>
                ) : (
                  <span className="text-gray-400 italic">No description provided</span>
                )}
              </DetailRow>

              {/* Version */}
              <DetailRow label="Version">
                <span className="inline-flex items-center rounded-full bg-indigo-100 px-2.5 py-0.5 text-xs font-medium text-indigo-800">
                  {formatVersion(plugin.version)}
                </span>
                {plugin.version && /^\d{8}$/.test(plugin.version) && (
                  <span className="ms-2 text-xs text-gray-500">
                    (raw: {plugin.version})
                  </span>
                )}
              </DetailRow>

              {/* Author */}
              <DetailRow label="Author">
                {plugin.author || <span className="text-gray-400">{'\u2014'}</span>}
              </DetailRow>

              {/* Company */}
              <DetailRow label="Company">
                {plugin.companyUrl ? (
                  <ExternalLink href={plugin.companyUrl} label={plugin.company || plugin.companyUrl} />
                ) : (
                  plugin.company || <span className="text-gray-400">{'\u2014'}</span>
                )}
              </DetailRow>

              {/* License */}
              <DetailRow label="License">
                {plugin.license ? (
                  <span className="inline-flex items-center rounded bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-700">
                    {plugin.license}
                  </span>
                ) : (
                  <span className="text-gray-400">{'\u2014'}</span>
                )}
              </DetailRow>

              {/* Repository */}
              <DetailRow label="Repository">
                <ExternalLink href={plugin.repository} />
              </DetailRow>

              {/* URL */}
              <DetailRow label="URL">
                <ExternalLink href={plugin.url} />
              </DetailRow>

              {/* Prefix */}
              <DetailRow label="Prefix">
                {plugin.prefix ? (
                  <code className="rounded bg-gray-100 px-2 py-0.5 text-xs font-mono text-gray-800">
                    {plugin.prefix}
                  </code>
                ) : (
                  <span className="text-gray-400">{'\u2014'}</span>
                )}
              </DetailRow>

              {/* Settings URL */}
              <DetailRow label="Settings URL">
                <ExternalLink href={plugin.settingsUrl} />
              </DetailRow>

              {/* Plugin Page URL */}
              <DetailRow label="Plugin Page URL">
                <ExternalLink href={plugin.pluginPageUrl} />
              </DetailRow>

              {/* Icon URL */}
              <DetailRow label="Icon URL">
                {plugin.iconUrl ? (
                  <div className="flex items-center gap-3">
                    <img
                      src={plugin.iconUrl}
                      alt={`${plugin.name} icon preview`}
                      width={32}
                      height={32}
                      className="h-8 w-8 rounded object-cover bg-gray-200"
                      loading="lazy"
                      decoding="async"
                    />
                    <ExternalLink href={plugin.iconUrl} />
                  </div>
                ) : (
                  <span className="text-gray-400">{'\u2014'}</span>
                )}
              </DetailRow>
            </dl>
          </div>
        </div>
      </section>

      {/* Plugin Configuration Data Card */}
      <section aria-labelledby="config-heading" className="mb-6">
        <div className="rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 id="config-heading" className="text-lg font-semibold text-gray-900">
              Configuration Data
            </h2>
          </div>
          <div className="px-6 py-4">
            {isPluginDataLoading ? (
              <div className="animate-pulse space-y-2" role="status" aria-label="Loading configuration data">
                <div className="h-4 w-48 rounded bg-gray-200" />
                <div className="h-24 w-full rounded bg-gray-200" />
                <span className="sr-only">Loading…</span>
              </div>
            ) : pluginDataRaw ? (
              <pre className="overflow-x-auto rounded-md bg-gray-50 p-4 text-sm font-mono text-gray-800 whitespace-pre-wrap overflow-wrap-break-word">
                {prettyPrintJson(pluginDataRaw)}
              </pre>
            ) : (
              <p className="text-sm text-gray-500 italic">
                No configuration data available for this plugin.
              </p>
            )}
          </div>
        </div>
      </section>

      {/* Version History / Patch Information Card */}
      <section aria-labelledby="version-heading" className="mb-6">
        <div className="rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 id="version-heading" className="text-lg font-semibold text-gray-900">
              Version &amp; Patch Information
            </h2>
          </div>
          <div className="px-6 py-4">
            <dl className="divide-y divide-gray-100">
              <DetailRow label="Plugin Version">
                {formatVersion(plugin.version)}
              </DetailRow>
              {patchVersion && (
                <DetailRow label="Patch Version">
                  {formatVersion(patchVersion)}
                  {/^\d{8}$/.test(patchVersion) && (
                    <span className="ms-2 text-xs text-gray-500">
                      (raw: {patchVersion})
                    </span>
                  )}
                </DetailRow>
              )}
              <DetailRow label="Status">
                <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">
                  Active
                </span>
              </DetailRow>
            </dl>
            <p className="mt-4 text-xs text-gray-400">
              In the legacy system, plugins used YYYYMMDD date-based integers as patch versions
              (e.g. 20181215, 20190227). When this format is detected, the version is displayed
              as a human-readable date.
            </p>
          </div>
        </div>
      </section>

      {/* Delete Confirmation Modal */}
      <Modal
        isVisible={showDeleteModal}
        title="Delete Plugin"
        onClose={handleDeleteCancel}
        footer={
          <>
            <button
              type="button"
              onClick={handleDeleteCancel}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleDeleteConfirm}
              disabled={deleteMutation.isPending}
              className="inline-flex items-center gap-1.5 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {deleteMutation.isPending ? 'Deleting…' : 'Delete Plugin'}
            </button>
          </>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to delete the plugin{' '}
          <strong className="font-semibold text-gray-900">{plugin.name}</strong>?
          This action cannot be undone.
        </p>
        {deleteMutation.isError && (
          <p className="mt-3 text-sm text-red-600" role="alert">
            {deleteMutation.error instanceof Error
              ? deleteMutation.error.message
              : 'An error occurred while deleting the plugin. Please try again.'}
          </p>
        )}
      </Modal>
    </div>
  );
}

export default PluginDetails;
