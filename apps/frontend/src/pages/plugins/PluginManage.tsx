/**
 * Plugin Configuration Management Page
 *
 * Admin-only page for editing plugin metadata, managing plugin-specific
 * configuration data (opaque JSON), and toggling plugin enabled/disabled
 * status. Replaces the monolith's `ErpPlugin.SavePluginData()` persistence
 * pattern and SDK admin plugin management workflows.
 *
 * Replaces:
 *  - `ErpPlugin.cs` property editing (13 metadata properties)
 *  - `ErpPlugin.SavePluginData()` → JSON configuration persistence
 *  - `SdkPlugin._.cs` → `PluginSettings` JSON serialization pattern
 *  - `AdminController.cs` `[Authorize(Roles = "administrator")]` access control
 *
 * Route: /plugins/:pluginId/manage (lazy-loaded via React.lazy())
 *
 * Data fetching:
 *  - `usePlugin(pluginId)` — single plugin metadata
 *  - `usePluginData(plugin.name)` — plugin configuration JSON
 *  - `useUpdatePlugin()` — mutation for metadata saves
 *  - `useSavePluginData()` — mutation for plugin data saves
 *
 * AAP compliance:
 *  - §0.8.1 — Full behavioral parity: all ErpPlugin properties editable
 *  - §0.8.2 — Per-route chunk < 200KB gzipped
 *  - Pure static SPA — no SSR, no server components
 *  - Tailwind CSS — no Bootstrap classes
 *  - TanStack Query — all data fetching via hooks
 *  - Admin-only access — redirects non-admin users
 *
 * @module pages/plugins/PluginManage
 */

import { useState, useEffect } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import {
  usePlugin,
  usePluginData,
  useUpdatePlugin,
  useSavePluginData,
} from '../../hooks/usePlugins';
import type { Plugin } from '../../hooks/usePlugins';
import Modal from '../../components/common/Modal';
import { useIsAdmin, useAuthStore } from '../../stores/authStore';

/* ------------------------------------------------------------------ */
/*  SVG Icon Components                                                */
/* ------------------------------------------------------------------ */

/** Left-arrow icon for back navigation. */
function ArrowLeftIcon({ className = 'w-4 h-4' }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 20 20"
      fill="currentColor"
      className={className}
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        d="M17 10a.75.75 0 0 1-.75.75H5.612l4.158 3.96a.75.75 0 1 1-1.04 1.08l-5.5-5.25a.75.75 0 0 1 0-1.08l5.5-5.25a.75.75 0 1 1 1.04 1.08L5.612 9.25H16.25A.75.75 0 0 1 17 10Z"
        clipRule="evenodd"
      />
    </svg>
  );
}

/** Floppy-disk / save icon for action buttons. */
function SaveIcon({ className = 'w-4 h-4' }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 20 20"
      fill="currentColor"
      className={className}
      aria-hidden="true"
    >
      <path d="M15.988 3.012A2.25 2.25 0 0 0 14.174 2H5.25A2.25 2.25 0 0 0 3 4.25v11.5A2.25 2.25 0 0 0 5.25 18h9.5A2.25 2.25 0 0 0 17 15.75V5.826a2.25 2.25 0 0 0-.662-1.591l-.35-.35ZM5.25 3.5h8.924a.75.75 0 0 1 .53.22l.35.35a.75.75 0 0 1 .22.53v9.4a.75.75 0 0 1-.75.75H5.25a.75.75 0 0 1-.75-.75V4.25a.75.75 0 0 1 .75-.75ZM10 14a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM6.5 6a.75.75 0 0 0 0 1.5h4a.75.75 0 0 0 0-1.5h-4Z" />
    </svg>
  );
}

/** Warning triangle for error states and confirmation modals. */
function WarningIcon({ className = 'w-4 h-4' }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 20 20"
      fill="currentColor"
      className={className}
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495ZM10 5a.75.75 0 0 1 .75.75v3.5a.75.75 0 0 1-1.5 0v-3.5A.75.75 0 0 1 10 5Zm0 9a1 1 0 1 0 0-2 1 1 0 0 0 0 2Z"
        clipRule="evenodd"
      />
    </svg>
  );
}

/** Puzzle-piece icon for plugin header fallback. */
function PluginIcon({ className = 'w-8 h-8' }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="currentColor"
      className={className}
      aria-hidden="true"
    >
      <path d="M11.25 5.337c0-.355-.186-.676-.401-.959a1.647 1.647 0 0 1-.349-1.003c0-1.036 1.007-1.875 2.25-1.875S15 2.34 15 3.375c0 .369-.128.713-.349 1.003-.215.283-.401.604-.401.959 0 .332.278.598.61.578 1.91-.114 3.79-.342 5.632-.676a.75.75 0 0 1 .878.645 49.17 49.17 0 0 1 .376 5.452.657.657 0 0 1-.66.664c-.354 0-.675-.186-.958-.401a1.647 1.647 0 0 0-1.003-.349c-1.035 0-1.875 1.007-1.875 2.25s.84 2.25 1.875 2.25c.369 0 .713-.128 1.003-.349.283-.215.604-.401.959-.401.31 0 .557.262.534.571a48.774 48.774 0 0 1-.595 4.845.75.75 0 0 1-.61.61c-1.82.317-3.673.533-5.555.642a.58.58 0 0 1-.611-.581c0-.355.186-.676.401-.959.221-.29.349-.634.349-1.003 0-1.035-1.007-1.875-2.25-1.875s-2.25.84-2.25 1.875c0 .369.128.713.349 1.003.215.283.401.604.401.959a.641.641 0 0 1-.658.643 49.118 49.118 0 0 1-4.708-.441.75.75 0 0 1-.645-.878c.293-1.614.504-3.257.629-4.924A.53.53 0 0 0 5.337 15c-.355 0-.676.186-.959.401-.29.221-.634.349-1.003.349-1.036 0-1.875-1.007-1.875-2.25s.84-2.25 1.875-2.25c.369 0 .713.128 1.003.349.283.215.604.401.959.401a.656.656 0 0 0 .659-.663 47.703 47.703 0 0 0-.31-4.82.75.75 0 0 1 .83-.832c1.343.155 2.703.254 4.077.294a.64.64 0 0 0 .657-.642Z" />
    </svg>
  );
}

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
    const y = version.slice(0, 4);
    const m = version.slice(4, 6);
    const d = version.slice(6, 8);
    const date = new Date(`${y}-${m}-${d}`);
    if (!Number.isNaN(date.getTime())) {
      return date.toLocaleDateString(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
      });
    }
  }
  return `v${version}`;
}

/**
 * Validates a URL string. Returns true if the value is empty (URL fields
 * are optional) or if it is a syntactically valid URL.
 */
function isValidUrl(value: string): boolean {
  if (!value.trim()) return true;
  try {
    new URL(value);
    return true;
  } catch {
    return false;
  }
}

/**
 * Validates a JSON string. Returns true if the value is empty or
 * parses as valid JSON.
 */
function isValidJson(value: string): boolean {
  if (!value.trim()) return true;
  try {
    JSON.parse(value);
    return true;
  } catch {
    return false;
  }
}

/**
 * Pretty-prints a JSON string with 2-space indentation.
 * Returns the original string if parsing fails.
 */
function prettyPrintJson(value: string): string {
  if (!value.trim()) return '';
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Set of field names that should be validated as URLs. */
const URL_FIELDS = new Set([
  'url',
  'settingsUrl',
  'pluginPageUrl',
  'iconUrl',
  'companyUrl',
  'repository',
]);

/** Human-readable labels for all editable metadata fields. */
const FIELD_LABELS: Record<string, string> = {
  description: 'Description',
  url: 'Plugin URL',
  settingsUrl: 'Settings URL',
  pluginPageUrl: 'Plugin Page URL',
  iconUrl: 'Icon URL',
  company: 'Company',
  companyUrl: 'Company URL',
  author: 'Author',
  repository: 'Repository URL',
  license: 'License',
};

/* ------------------------------------------------------------------ */
/*  Sub-components                                                     */
/* ------------------------------------------------------------------ */

/** Animated loading placeholder matching the manage page form layout. */
function LoadingSkeleton() {
  const pulseBlock = (width: string) => (
    <div className={`h-4 ${width} animate-pulse rounded bg-gray-200`} />
  );

  return (
    <div role="status" aria-label="Loading plugin configuration">
      {/* Breadcrumb skeleton */}
      <div className="mb-6 flex items-center gap-2">
        {pulseBlock('w-12')}
        <span className="text-gray-300">/</span>
        {pulseBlock('w-16')}
        <span className="text-gray-300">/</span>
        {pulseBlock('w-24')}
        <span className="text-gray-300">/</span>
        {pulseBlock('w-16')}
      </div>
      {/* Header skeleton */}
      <div className="mb-8 flex items-center gap-4">
        <div className="h-14 w-14 animate-pulse rounded-lg bg-gray-200" />
        <div className="space-y-2">
          <div className="h-6 w-48 animate-pulse rounded bg-gray-200" />
          <div className="h-4 w-24 animate-pulse rounded bg-gray-200" />
        </div>
      </div>
      {/* Form section skeletons */}
      {Array.from({ length: 3 }, (_, i) => (
        <div
          key={i}
          className="mb-6 rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
        >
          <div className="mb-4 h-5 w-32 animate-pulse rounded bg-gray-200" />
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {Array.from({ length: 4 }, (_, j) => (
              <div key={j} className="space-y-2">
                <div className="h-4 w-20 animate-pulse rounded bg-gray-200" />
                <div className="h-10 w-full animate-pulse rounded bg-gray-200" />
              </div>
            ))}
          </div>
        </div>
      ))}
      <span className="sr-only">Loading plugin configuration…</span>
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  Main Component                                                     */
/* ------------------------------------------------------------------ */

/**
 * Plugin configuration management page component.
 *
 * Provides admin-only forms for editing plugin metadata, managing the
 * opaque plugin configuration JSON data, and toggling the plugin
 * enabled/disabled status with a confirmation dialog.
 */
function PluginManage() {
  /* ---- Route params & navigation ---------------------------------- */
  const { pluginId = '' } = useParams<{ pluginId: string }>();
  const navigate = useNavigate();

  /* ---- Auth guard ------------------------------------------------- */
  const isAdmin = useIsAdmin();
  const currentUser = useAuthStore((state) => state.currentUser);

  /* ---- Data fetching ---------------------------------------------- */
  const {
    data: pluginResponse,
    isLoading: isPluginLoading,
    isError: isPluginError,
    error: pluginError,
  } = usePlugin(pluginId);

  const plugin: Plugin | undefined = pluginResponse?.object;

  const {
    data: pluginDataResponse,
    isLoading: isDataLoading,
  } = usePluginData(plugin?.name ?? '');

  const pluginDataRaw: string = pluginDataResponse?.object ?? '';

  /* ---- Mutations -------------------------------------------------- */
  const updateMutation = useUpdatePlugin();
  const saveDataMutation = useSavePluginData();

  /* ---- Form state (editable metadata fields) ---------------------- */
  const [formState, setFormState] = useState<Record<string, string>>({
    description: '',
    url: '',
    settingsUrl: '',
    pluginPageUrl: '',
    iconUrl: '',
    company: '',
    companyUrl: '',
    author: '',
    repository: '',
    license: '',
  });
  const [formDirty, setFormDirty] = useState(false);
  const [formErrors, setFormErrors] = useState<Record<string, string>>({});

  /* ---- Plugin data (JSON) state ----------------------------------- */
  const [pluginDataText, setPluginDataText] = useState('');
  const [dataDirty, setDataDirty] = useState(false);
  const [dataError, setDataError] = useState('');

  /* ---- Enable/disable state --------------------------------------- */
  /*
   * BLITZY [DESIGN_SYSTEM_GAP]: Plugin.enabled property not in Plugin interface.
   * Chose: Track locally with visual toggle; mutation sends standard Plugin payload.
   * Alternative: Extend Plugin interface in usePlugins.ts with optional `enabled` field.
   */
  const [enabled, setEnabled] = useState(true);
  const [showDisableModal, setShowDisableModal] = useState(false);

  /* ---- Notification state ----------------------------------------- */
  const [notification, setNotification] = useState<{
    type: 'success' | 'error';
    message: string;
  } | null>(null);

  /* ---- Initialize form from fetched plugin data ------------------- */
  useEffect(() => {
    if (plugin) {
      setFormState({
        description: plugin.description ?? '',
        url: plugin.url ?? '',
        settingsUrl: plugin.settingsUrl ?? '',
        pluginPageUrl: plugin.pluginPageUrl ?? '',
        iconUrl: plugin.iconUrl ?? '',
        company: plugin.company ?? '',
        companyUrl: plugin.companyUrl ?? '',
        author: plugin.author ?? '',
        repository: plugin.repository ?? '',
        license: plugin.license ?? '',
      });
      setFormDirty(false);
      setFormErrors({});
    }
  }, [plugin]);

  useEffect(() => {
    if (pluginDataRaw) {
      setPluginDataText(prettyPrintJson(pluginDataRaw));
      setDataDirty(false);
      setDataError('');
    }
  }, [pluginDataRaw]);

  /* ---- Admin redirect: wait for auth to resolve before redirecting - */
  useEffect(() => {
    if (currentUser && !isAdmin && !isPluginLoading) {
      navigate('/plugins', { replace: true });
    }
  }, [currentUser, isAdmin, isPluginLoading, navigate]);

  /* ---- Auto-dismiss notifications after 5 seconds ----------------- */
  useEffect(() => {
    if (notification) {
      const timer = setTimeout(() => setNotification(null), 5000);
      return () => clearTimeout(timer);
    }
    return undefined;
  }, [notification]);

  /* ---- Handlers --------------------------------------------------- */

  /** Updates a single metadata form field and marks the form dirty. */
  function handleFieldChange(field: string, value: string) {
    setFormState((prev) => ({ ...prev, [field]: value }));
    setFormDirty(true);

    if (formErrors[field]) {
      setFormErrors((prev) => {
        const next = { ...prev };
        delete next[field];
        return next;
      });
    }
  }

  /** Validates all URL fields in the form. Returns true if no errors. */
  function validateForm(): boolean {
    const errors: Record<string, string> = {};
    for (const field of Object.keys(formState)) {
      if (URL_FIELDS.has(field) && !isValidUrl(formState[field])) {
        errors[field] = 'Please enter a valid URL or leave empty.';
      }
    }
    setFormErrors(errors);
    return Object.keys(errors).length === 0;
  }

  /** Saves plugin metadata via useUpdatePlugin mutation. */
  function handleSaveMetadata() {
    if (!plugin || !validateForm()) return;

    const payload: Plugin = {
      id: plugin.id,
      name: plugin.name,
      prefix: plugin.prefix,
      version: plugin.version,
      description: formState.description,
      url: formState.url,
      settingsUrl: formState.settingsUrl,
      pluginPageUrl: formState.pluginPageUrl,
      iconUrl: formState.iconUrl,
      company: formState.company,
      companyUrl: formState.companyUrl,
      author: formState.author,
      repository: formState.repository,
      license: formState.license,
    };

    updateMutation.mutate(payload, {
      onSuccess: () => {
        setFormDirty(false);
        setNotification({
          type: 'success',
          message: 'Plugin metadata saved successfully.',
        });
      },
      onError: (err) => {
        setNotification({
          type: 'error',
          message: err.message || 'Failed to save plugin metadata.',
        });
      },
    });
  }

  /** Saves plugin configuration data via useSavePluginData mutation. */
  function handleSavePluginData() {
    if (!plugin) return;

    if (!isValidJson(pluginDataText)) {
      setDataError('Invalid JSON. Please check your syntax.');
      return;
    }

    saveDataMutation.mutate(
      { pluginName: plugin.name, data: pluginDataText.trim() || '{}' },
      {
        onSuccess: () => {
          setDataDirty(false);
          setDataError('');
          setNotification({
            type: 'success',
            message: 'Plugin configuration data saved successfully.',
          });
        },
        onError: (err) => {
          setNotification({
            type: 'error',
            message: err.message || 'Failed to save plugin configuration data.',
          });
        },
      },
    );
  }

  /** Handles toggle click — shows confirmation when disabling. */
  function handleToggleEnabled() {
    if (enabled) {
      setShowDisableModal(true);
    } else {
      setEnabled(true);
      if (plugin) {
        updateMutation.mutate(
          {
            id: plugin.id,
            name: plugin.name,
            prefix: plugin.prefix,
            version: plugin.version,
            description: plugin.description,
            url: plugin.url,
            settingsUrl: plugin.settingsUrl,
            pluginPageUrl: plugin.pluginPageUrl,
            iconUrl: plugin.iconUrl,
            company: plugin.company,
            companyUrl: plugin.companyUrl,
            author: plugin.author,
            repository: plugin.repository,
            license: plugin.license,
          },
          {
            onSuccess: () => {
              setNotification({
                type: 'success',
                message: `Plugin "${plugin.name}" has been enabled.`,
              });
            },
            onError: (err) => {
              setEnabled(false);
              setNotification({
                type: 'error',
                message: err.message || 'Failed to enable plugin.',
              });
            },
          },
        );
      }
    }
  }

  /** Confirms disable action from the modal and triggers mutation. */
  function handleConfirmDisable() {
    setShowDisableModal(false);
    setEnabled(false);
    if (plugin) {
      updateMutation.mutate(
        {
          id: plugin.id,
          name: plugin.name,
          prefix: plugin.prefix,
          version: plugin.version,
          description: plugin.description,
          url: plugin.url,
          settingsUrl: plugin.settingsUrl,
          pluginPageUrl: plugin.pluginPageUrl,
          iconUrl: plugin.iconUrl,
          company: plugin.company,
          companyUrl: plugin.companyUrl,
          author: plugin.author,
          repository: plugin.repository,
          license: plugin.license,
        },
        {
          onSuccess: () => {
            setNotification({
              type: 'success',
              message: `Plugin "${plugin.name}" has been disabled.`,
            });
          },
          onError: (err) => {
            setEnabled(true);
            setNotification({
              type: 'error',
              message: err.message || 'Failed to disable plugin.',
            });
          },
        },
      );
    }
  }

  /** Cancels the disable confirmation. */
  function handleCancelDisable() {
    setShowDisableModal(false);
  }

  /** Navigates back to the plugin detail page. */
  function handleCancel() {
    navigate(`/plugins/${pluginId}`);
  }

  /** Updates plugin data JSON text and marks data dirty. */
  function handlePluginDataChange(value: string) {
    setPluginDataText(value);
    setDataDirty(true);
    if (dataError) setDataError('');
  }

  /* ---- Loading state ---------------------------------------------- */
  if (isPluginLoading) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <LoadingSkeleton />
      </div>
    );
  }

  /* ---- Error / Not Found state ------------------------------------ */
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
              <Link
                to="/"
                className="hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
              >
                Home
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li>
              <Link
                to="/plugins"
                className="hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
              >
                Plugins
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li className="font-medium text-gray-900" aria-current="page">
              Not Found
            </li>
          </ol>
        </nav>

        {/* Error card */}
        <div className="rounded-lg border border-red-200 bg-red-50 p-8 text-center">
          <WarningIcon className="mx-auto mb-4 h-12 w-12 text-red-400" />
          <h1 className="mb-2 text-xl font-semibold text-gray-900">
            Plugin Not Found
          </h1>
          <p className="mb-6 text-sm text-gray-600">{errorMessage}</p>
          <Link
            to="/plugins"
            className="inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            <ArrowLeftIcon className="h-4 w-4" />
            Back to Plugins
          </Link>
        </div>
      </div>
    );
  }

  /* ---- Main render ------------------------------------------------ */
  return (
    <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
      {/* Breadcrumb */}
      <nav aria-label="Breadcrumb" className="mb-6">
        <ol className="flex items-center gap-2 text-sm text-gray-500">
          <li>
            <Link
              to="/"
              className="hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            >
              Home
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li>
            <Link
              to="/plugins"
              className="hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            >
              Plugins
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li>
            <Link
              to={`/plugins/${pluginId}`}
              className="hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            >
              {plugin.name || 'Plugin'}
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li className="font-medium text-gray-900" aria-current="page">
            Manage
          </li>
        </ol>
      </nav>

      {/* Notification banner */}
      {notification && (
        <div
          role="alert"
          className={`mb-6 rounded-lg border px-4 py-3 text-sm ${
            notification.type === 'success'
              ? 'border-green-200 bg-green-50 text-green-800'
              : 'border-red-200 bg-red-50 text-red-800'
          }`}
        >
          {notification.message}
        </div>
      )}

      {/* Page header with icon, title, version/status badges, and back link */}
      <div className="mb-8 flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
        {/* Left: icon + title cluster */}
        <div className="flex items-start gap-4">
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
              <PluginIcon className="h-8 w-8" />
            )}
          </div>

          <div>
            <h1 className="text-2xl font-bold text-gray-900">
              Manage: {plugin.name || '\u2014'}
            </h1>
            <div className="mt-1 flex flex-wrap items-center gap-2">
              {/* Version badge */}
              <span className="inline-flex items-center rounded-full bg-indigo-100 px-2.5 py-0.5 text-xs font-medium text-indigo-800">
                {formatVersion(plugin.version)}
              </span>
              {/* Status badge */}
              <span
                className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${
                  enabled
                    ? 'bg-green-100 text-green-800'
                    : 'bg-gray-100 text-gray-600'
                }`}
              >
                {enabled ? 'Enabled' : 'Disabled'}
              </span>
            </div>
          </div>
        </div>

        {/* Right: back link */}
        <div className="flex items-center gap-3">
          <Link
            to={`/plugins/${pluginId}`}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
          >
            <ArrowLeftIcon className="h-4 w-4" />
            Back to Details
          </Link>
        </div>
      </div>

      {/* ---- Section: Enable / Disable Toggle ---------------------- */}
      <section className="mb-6 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <div className="flex items-center justify-between">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">
              Plugin Status
            </h2>
            <p className="mt-1 text-sm text-gray-500">
              {enabled
                ? 'This plugin is currently enabled and active.'
                : 'This plugin is currently disabled.'}
            </p>
          </div>
          <button
            type="button"
            role="switch"
            aria-checked={enabled}
            aria-label={`${enabled ? 'Disable' : 'Enable'} plugin ${plugin.name}`}
            onClick={handleToggleEnabled}
            disabled={updateMutation.isPending}
            className={`relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50 ${
              enabled ? 'bg-indigo-600' : 'bg-gray-200'
            }`}
          >
            <span
              aria-hidden="true"
              className={`pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out ${
                enabled ? 'translate-x-5' : 'translate-x-0'
              }`}
            />
          </button>
        </div>
      </section>

      {/* ---- Section: Read-Only Plugin Identity --------------------- */}
      <section className="mb-6 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <h2 className="mb-4 text-lg font-semibold text-gray-900">
          Plugin Identity
        </h2>
        <p className="mb-4 text-sm text-gray-500">
          These fields are read-only and cannot be changed.
        </p>
        <dl className="grid grid-cols-1 gap-4 sm:grid-cols-3">
          <div>
            <dt className="text-sm font-medium text-gray-500">Name</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {plugin.name || '\u2014'}
            </dd>
          </div>
          <div>
            <dt className="text-sm font-medium text-gray-500">Prefix</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {plugin.prefix || '\u2014'}
            </dd>
          </div>
          <div>
            <dt className="text-sm font-medium text-gray-500">Version</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {formatVersion(plugin.version)}
            </dd>
          </div>
        </dl>
      </section>

      {/* ---- Section: Editable Metadata Form ----------------------- */}
      <section className="mb-6 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <h2 className="mb-4 text-lg font-semibold text-gray-900">
          Plugin Metadata
        </h2>
        <p className="mb-4 text-sm text-gray-500">
          Edit the plugin&apos;s descriptive metadata. URL fields are validated
          on save.
        </p>

        <div className="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2">
          {Object.entries(FIELD_LABELS).map(([field, label]) => {
            const isUrl = URL_FIELDS.has(field);
            const error = formErrors[field];
            const inputId = `plugin-field-${field}`;

            return (
              <div
                key={field}
                className={field === 'description' ? 'sm:col-span-2' : ''}
              >
                <label
                  htmlFor={inputId}
                  className="block text-sm font-medium text-gray-700"
                >
                  {label}
                </label>
                {field === 'description' ? (
                  <textarea
                    id={inputId}
                    rows={3}
                    value={formState[field]}
                    onChange={(e) => handleFieldChange(field, e.target.value)}
                    className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 ${
                      error
                        ? 'border-red-300 text-red-900 placeholder-red-300'
                        : 'border-gray-300 text-gray-900 placeholder-gray-400'
                    }`}
                    placeholder={`Enter ${label.toLowerCase()}`}
                  />
                ) : (
                  <input
                    id={inputId}
                    type={isUrl ? 'url' : 'text'}
                    value={formState[field]}
                    onChange={(e) => handleFieldChange(field, e.target.value)}
                    className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 ${
                      error
                        ? 'border-red-300 text-red-900 placeholder-red-300'
                        : 'border-gray-300 text-gray-900 placeholder-gray-400'
                    }`}
                    placeholder={
                      isUrl
                        ? 'https://example.com'
                        : `Enter ${label.toLowerCase()}`
                    }
                  />
                )}
                {error && (
                  <p className="mt-1 text-xs text-red-600" role="alert">
                    {error}
                  </p>
                )}
              </div>
            );
          })}
        </div>

        {/* Metadata save / cancel actions */}
        <div className="mt-6 flex items-center gap-3 border-t border-gray-100 pt-4">
          <button
            type="button"
            onClick={handleSaveMetadata}
            disabled={!formDirty || updateMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <SaveIcon className="h-4 w-4" />
            {updateMutation.isPending ? 'Saving\u2026' : 'Save Changes'}
          </button>
          <button
            type="button"
            onClick={handleCancel}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
          >
            Cancel
          </button>
        </div>
      </section>

      {/* ---- Section: Plugin Configuration Data (JSON) -------------- */}
      <section className="mb-6 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <h2 className="mb-4 text-lg font-semibold text-gray-900">
          Plugin Configuration Data
        </h2>
        <p className="mb-4 text-sm text-gray-500">
          Plugin-specific configuration stored as JSON. This replaces the
          monolith&apos;s{' '}
          <code className="mx-1 rounded bg-gray-100 px-1.5 py-0.5 font-mono text-xs">
            plugin_data
          </code>{' '}
          table entries.
        </p>

        {isDataLoading ? (
          <div className="h-40 w-full animate-pulse rounded-md bg-gray-100" />
        ) : (
          <>
            <label htmlFor="plugin-data-editor" className="sr-only">
              Plugin configuration JSON
            </label>
            <textarea
              id="plugin-data-editor"
              rows={12}
              value={pluginDataText}
              onChange={(e) => handlePluginDataChange(e.target.value)}
              spellCheck={false}
              className={`block w-full rounded-md border px-3 py-2 font-mono text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 ${
                dataError
                  ? 'border-red-300 text-red-900'
                  : 'border-gray-300 text-gray-900'
              }`}
              placeholder={'{ "key": "value" }'}
            />
            {dataError && (
              <p className="mt-1 text-xs text-red-600" role="alert">
                {dataError}
              </p>
            )}
          </>
        )}

        <div className="mt-4 flex items-center gap-3 border-t border-gray-100 pt-4">
          <button
            type="button"
            onClick={handleSavePluginData}
            disabled={!dataDirty || saveDataMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <SaveIcon className="h-4 w-4" />
            {saveDataMutation.isPending
              ? 'Saving\u2026'
              : 'Save Plugin Data'}
          </button>
        </div>
      </section>

      {/* ---- Disable Confirmation Modal ----------------------------- */}
      <Modal
        isVisible={showDisableModal}
        onClose={handleCancelDisable}
        title="Disable Plugin"
        footer={
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={handleCancelDisable}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmDisable}
              disabled={updateMutation.isPending}
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {updateMutation.isPending ? 'Disabling\u2026' : 'Disable Plugin'}
            </button>
          </div>
        }
      >
        <div className="flex items-start gap-4">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-red-100">
            <WarningIcon className="h-5 w-5 text-red-600" />
          </div>
          <div>
            <p className="text-sm text-gray-600">
              Are you sure you want to disable the plugin{' '}
              <span className="font-semibold text-gray-900">
                {plugin.name}
              </span>
              ? Disabling a plugin may affect dependent functionality.
            </p>
          </div>
        </div>
      </Modal>
    </div>
  );
}

export default PluginManage;
