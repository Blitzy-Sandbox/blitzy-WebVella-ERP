import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  usePlugins,
  useRegisterPlugin,
  useDeletePlugin,
} from '../../hooks/usePlugins';
import type { Plugin } from '../../hooks/usePlugins';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Modal, { ModalSize } from '../../components/common/Modal';

/**
 * Intersection type that adds an index signature to Plugin so it satisfies
 * the DataTable generic constraint `T extends Record<string, unknown>`.
 */
type PluginRow = Plugin & Record<string, unknown>;

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Default registration form values. Matches Omit<Plugin, 'id'>. */
const INITIAL_FORM: Omit<Plugin, 'id'> = {
  name: '',
  prefix: '',
  url: '',
  description: '',
  version: '1',
  company: '',
  companyUrl: '',
  author: '',
  repository: '',
  license: 'MIT',
  settingsUrl: '',
  pluginPageUrl: '',
  iconUrl: '',
};

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

/** Truncate text to `max` characters, appending an ellipsis when needed. */
function truncateText(text: string | undefined | null, max = 80): string {
  if (!text) return '';
  return text.length <= max ? text : `${text.slice(0, max)}\u2026`;
}

/* ------------------------------------------------------------------ */
/*  Inline SVG Icons (currentColor, sized via className)              */
/* ------------------------------------------------------------------ */

/** Cube icon representing a plugin / extension package. */
function PluginIcon({ className = 'w-6 h-6' }: { className?: string }) {
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

/** Circular-arrows refresh icon. */
function RefreshIcon({ className = 'w-5 h-5' }: { className?: string }) {
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
      <path d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
    </svg>
  );
}

/** Plus icon for "Register" actions. */
function PlusIcon({ className = 'w-5 h-5' }: { className?: string }) {
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
      <path d="M12 5v14m-7-7h14" />
    </svg>
  );
}

/** Trash can icon for delete actions. */
function TrashIcon({ className = 'w-4 h-4' }: { className?: string }) {
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

/** Eye icon for "View" actions. */
function EyeIcon({ className = 'w-4 h-4' }: { className?: string }) {
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
      <path d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
      <path d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
    </svg>
  );
}

/** Cog / gear icon for "Manage" actions. */
function CogIcon({ className = 'w-4 h-4' }: { className?: string }) {
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
      <path d="M10.325 4.317a1.724 1.724 0 013.35 0l.215.697a1.724 1.724 0 002.573.992l.591-.41a1.724 1.724 0 012.11 2.11l-.41.591a1.724 1.724 0 00.992 2.573l.697.215a1.724 1.724 0 010 3.35l-.697.215a1.724 1.724 0 00-.992 2.573l.41.591a1.724 1.724 0 01-2.11 2.11l-.591-.41a1.724 1.724 0 00-2.573.992l-.215.697a1.724 1.724 0 01-3.35 0l-.215-.697a1.724 1.724 0 00-2.573-.992l-.591.41a1.724 1.724 0 01-2.11-2.11l.41-.591a1.724 1.724 0 00-.992-2.573l-.697-.215a1.724 1.724 0 010-3.35l.697-.215a1.724 1.724 0 00.992-2.573l-.41-.591a1.724 1.724 0 012.11-2.11l.591.41a1.724 1.724 0 002.573-.992l.215-.697z" />
      <circle cx="12" cy="12" r="3" />
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
/*  Main Component                                                     */
/* ------------------------------------------------------------------ */

/**
 * Plugin registry listing page.
 *
 * Displays all registered plugins in a sortable, filterable data grid
 * with inline actions for viewing, managing, and deleting plugins,
 * plus a modal-based registration flow.
 *
 * Route: /plugins (lazy-loaded, admin-protected)
 */
function PluginList() {
  const navigate = useNavigate();

  /* ---- data hooks ------------------------------------------------ */
  const { data: pluginsResponse, isLoading, isError, error, refetch } = usePlugins();
  const registerMutation = useRegisterPlugin();
  const deleteMutation = useDeletePlugin();

  const plugins: Plugin[] = pluginsResponse?.object ?? [];

  /* ---- local UI state -------------------------------------------- */
  const [showRegisterModal, setShowRegisterModal] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<Plugin | null>(null);
  const [formData, setFormData] = useState<Omit<Plugin, 'id'>>(INITIAL_FORM);
  const [formError, setFormError] = useState('');

  /* ---- handlers -------------------------------------------------- */

  /** Open the registration modal with a clean form. */
  function handleOpenRegister() {
    setFormData(INITIAL_FORM);
    setFormError('');
    registerMutation.reset();
    setShowRegisterModal(true);
  }

  /** Close the registration modal. */
  function handleCloseRegister() {
    setShowRegisterModal(false);
  }

  /** Update a single form field by key. */
  function handleFormChange(key: keyof Omit<Plugin, 'id'>, value: string) {
    setFormData((prev) => ({ ...prev, [key]: value }));
  }

  /** Submit the registration form. */
  function handleRegisterSubmit(e: React.FormEvent) {
    e.preventDefault();
    setFormError('');

    if (!formData.name.trim()) {
      setFormError('Plugin name is required.');
      return;
    }

    registerMutation.mutate(formData, {
      onSuccess: () => {
        setShowRegisterModal(false);
      },
      onError: (err: unknown) => {
        const message =
          err instanceof Error ? err.message : 'Failed to register plugin.';
        setFormError(message);
      },
    });
  }

  /** Open the delete confirmation modal for a specific plugin. */
  function handleDeleteClick(plugin: Plugin) {
    setDeleteTarget(plugin);
    deleteMutation.reset();
    setShowDeleteConfirm(true);
  }

  /** Execute the deletion after user confirms. */
  function handleDeleteConfirm() {
    if (!deleteTarget) return;

    deleteMutation.mutate(deleteTarget.id, {
      onSuccess: () => {
        setShowDeleteConfirm(false);
        setDeleteTarget(null);
      },
    });
  }

  /** Cancel the deletion. */
  function handleDeleteCancel() {
    setShowDeleteConfirm(false);
    setDeleteTarget(null);
  }

  /** Navigate to the plugin details page. */
  function handleViewDetails(plugin: Plugin) {
    navigate(`/plugins/${plugin.id}`);
  }

  /** Navigate to the plugin management page. */
  function handleManage(plugin: Plugin) {
    navigate(`/plugins/${plugin.id}/manage`);
  }

  /* ---- column definitions ---------------------------------------- */

  const columns: DataTableColumn<PluginRow>[] = [
    {
      id: 'icon',
      label: '',
      width: '48px',
      cell: (_value: unknown, record: PluginRow) => (
        <span className="flex items-center justify-center">
          {record.iconUrl ? (
            <img
              src={record.iconUrl}
              alt=""
              aria-hidden="true"
              width={24}
              height={24}
              className="w-6 h-6 rounded object-cover bg-gray-200"
              loading="lazy"
              decoding="async"
            />
          ) : (
            <span className="text-indigo-500">
              <PluginIcon className="w-6 h-6" />
            </span>
          )}
        </span>
      ),
    },
    {
      id: 'name',
      label: 'Name',
      accessorKey: 'name',
      sortable: true,
      searchable: true,
      cell: (_value: unknown, record: PluginRow) => (
        <Link
          to={`/plugins/${record.id}`}
          className="font-medium text-indigo-600 hover:text-indigo-900 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
        >
          {record.name || '\u2014'}
        </Link>
      ),
    },
    {
      id: 'description',
      label: 'Description',
      accessorKey: 'description',
      searchable: true,
      cell: (value: unknown) => (
        <span
          className="text-gray-600 text-sm"
          title={typeof value === 'string' ? value : undefined}
        >
          {truncateText(typeof value === 'string' ? value : '')}
        </span>
      ),
    },
    {
      id: 'version',
      label: 'Version',
      accessorKey: 'version',
      sortable: true,
      width: '120px',
      cell: (value: unknown) => (
        <span className="text-sm font-mono text-gray-700">
          {formatVersion(typeof value === 'string' ? value : String(value ?? ''))}
        </span>
      ),
    },
    {
      id: 'author',
      label: 'Author',
      accessorKey: 'author',
      sortable: true,
      width: '140px',
      cell: (value: unknown) => (
        <span className="text-sm text-gray-700">
          {typeof value === 'string' && value ? value : '\u2014'}
        </span>
      ),
    },
    {
      id: 'status',
      label: 'Status',
      width: '100px',
      cell: () => (
        <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">
          Active
        </span>
      ),
    },
    {
      id: 'actions',
      label: 'Actions',
      width: '130px',
      cell: (_value: unknown, record: PluginRow) => (
        <span className="flex items-center gap-1">
          <button
            type="button"
            onClick={() => handleViewDetails(record)}
            className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 hover:bg-gray-100 hover:text-indigo-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            title="View details"
            aria-label={`View details for ${record.name}`}
          >
            <EyeIcon />
          </button>
          <button
            type="button"
            onClick={() => handleManage(record)}
            className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 hover:bg-gray-100 hover:text-indigo-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            title="Manage plugin"
            aria-label={`Manage ${record.name}`}
          >
            <CogIcon />
          </button>
          <button
            type="button"
            onClick={() => handleDeleteClick(record)}
            className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 hover:bg-gray-100 hover:text-red-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500"
            title="Delete plugin"
            aria-label={`Delete ${record.name}`}
          >
            <TrashIcon />
          </button>
        </span>
      ),
    },
  ];

  /* ---- loading state --------------------------------------------- */

  if (isLoading) {
    return (
      <section className="space-y-6 p-6" aria-busy="true" aria-label="Loading plugins">
        {/* Page header skeleton */}
        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="space-y-2">
            <div className="h-7 w-32 animate-pulse rounded bg-gray-200" />
            <div className="h-4 w-52 animate-pulse rounded bg-gray-200" />
          </div>
          <div className="flex gap-2">
            <div className="h-9 w-36 animate-pulse rounded bg-gray-200" />
            <div className="h-9 w-24 animate-pulse rounded bg-gray-200" />
          </div>
        </div>
        {/* Table skeleton */}
        <div className="rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="space-y-3 p-4">
            {Array.from({ length: 5 }, (_, i) => (
              <div key={i} className="flex gap-4">
                <div className="h-5 w-8 animate-pulse rounded bg-gray-200" />
                <div className="h-5 flex-1 animate-pulse rounded bg-gray-200" />
                <div className="h-5 flex-1 animate-pulse rounded bg-gray-200" />
                <div className="h-5 w-20 animate-pulse rounded bg-gray-200" />
                <div className="h-5 w-24 animate-pulse rounded bg-gray-200" />
                <div className="h-5 w-16 animate-pulse rounded bg-gray-200" />
              </div>
            ))}
          </div>
        </div>
      </section>
    );
  }

  /* ---- error state ------------------------------------------------ */

  if (isError) {
    const errMessage =
      error instanceof Error ? error.message : 'An unexpected error occurred.';
    return (
      <section className="p-6" aria-label="Error loading plugins">
        <div className="mx-auto max-w-lg rounded-lg border border-red-200 bg-red-50 p-8 text-center">
          <WarningIcon className="mx-auto h-10 w-10 text-red-400" />
          <h2 className="mt-4 text-lg font-semibold text-red-800">
            Failed to load plugins
          </h2>
          <p className="mt-2 text-sm text-red-600">{errMessage}</p>
          <button
            type="button"
            onClick={() => refetch()}
            className="mt-6 inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500"
          >
            <RefreshIcon className="w-4 h-4" />
            Retry
          </button>
        </div>
      </section>
    );
  }

  /* ---- empty state ------------------------------------------------ */

  if (plugins.length === 0) {
    return (
      <section className="p-6" aria-label="No plugins">
        {/* Page header (still shown when empty) */}
        <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">Plugins</h1>
            <p className="mt-1 text-sm text-gray-500">
              Manage registered extensions
            </p>
          </div>
        </div>

        <div className="mx-auto max-w-md rounded-lg border border-gray-200 bg-white p-10 text-center shadow-sm">
          <span className="mx-auto flex h-14 w-14 items-center justify-center rounded-full bg-indigo-100 text-indigo-600">
            <PluginIcon className="w-8 h-8" />
          </span>
          <h2 className="mt-4 text-lg font-semibold text-gray-900">
            No plugins registered
          </h2>
          <p className="mt-2 text-sm text-gray-500">
            Get started by registering your first plugin to extend the
            platform&rsquo;s functionality.
          </p>
          <button
            type="button"
            onClick={handleOpenRegister}
            className="mt-6 inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
          >
            <PlusIcon className="w-4 h-4" />
            Register your first plugin
          </button>
        </div>

        {/* Registration modal (available even in empty state) */}
        {renderRegisterModal()}
      </section>
    );
  }

  /* ---- modal render helpers -------------------------------------- */

  /** Reusable helper: registration modal JSX. */
  function renderRegisterModal() {
    return (
      <Modal
        isVisible={showRegisterModal}
        title="Register Plugin"
        size={ModalSize.Normal}
        onClose={handleCloseRegister}
        footer={
          <div className="flex items-center justify-end gap-3">
            <button
              type="button"
              onClick={handleCloseRegister}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            >
              Cancel
            </button>
            <button
              type="submit"
              form="register-plugin-form"
              disabled={registerMutation.isPending}
              className="inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {registerMutation.isPending ? 'Registering\u2026' : 'Register'}
            </button>
          </div>
        }
      >
        <form
          id="register-plugin-form"
          onSubmit={handleRegisterSubmit}
          className="space-y-4"
          noValidate
        >
          {formError && (
            <div
              role="alert"
              className="rounded-md bg-red-50 p-3 text-sm text-red-700"
            >
              {formError}
            </div>
          )}

          {/* Name (required) */}
          <div>
            <label
              htmlFor="plugin-name"
              className="block text-sm font-medium text-gray-700"
            >
              Name <span className="text-red-500">*</span>
            </label>
            <input
              id="plugin-name"
              type="text"
              required
              value={formData.name}
              onChange={(e) => handleFormChange('name', e.target.value)}
              placeholder="e.g. my-plugin"
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
            />
          </div>

          {/* Description */}
          <div>
            <label
              htmlFor="plugin-description"
              className="block text-sm font-medium text-gray-700"
            >
              Description
            </label>
            <textarea
              id="plugin-description"
              rows={2}
              value={formData.description}
              onChange={(e) => handleFormChange('description', e.target.value)}
              placeholder="Short description of what this plugin does"
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
            />
          </div>

          {/* Author + Version (side by side) */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <label
                htmlFor="plugin-author"
                className="block text-sm font-medium text-gray-700"
              >
                Author
              </label>
              <input
                id="plugin-author"
                type="text"
                value={formData.author}
                onChange={(e) => handleFormChange('author', e.target.value)}
                placeholder="Author name"
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
              />
            </div>
            <div>
              <label
                htmlFor="plugin-version"
                className="block text-sm font-medium text-gray-700"
              >
                Version
              </label>
              <input
                id="plugin-version"
                type="text"
                value={formData.version}
                onChange={(e) => handleFormChange('version', e.target.value)}
                placeholder="1"
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
              />
            </div>
          </div>

          {/* URL + Repository (side by side) */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <label
                htmlFor="plugin-url"
                className="block text-sm font-medium text-gray-700"
              >
                URL
              </label>
              <input
                id="plugin-url"
                type="url"
                value={formData.url}
                onChange={(e) => handleFormChange('url', e.target.value)}
                placeholder="https://example.com"
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
              />
            </div>
            <div>
              <label
                htmlFor="plugin-repository"
                className="block text-sm font-medium text-gray-700"
              >
                Repository
              </label>
              <input
                id="plugin-repository"
                type="url"
                value={formData.repository}
                onChange={(e) => handleFormChange('repository', e.target.value)}
                placeholder="https://github.com/org/repo"
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
              />
            </div>
          </div>

          {/* License */}
          <div>
            <label
              htmlFor="plugin-license"
              className="block text-sm font-medium text-gray-700"
            >
              License
            </label>
            <input
              id="plugin-license"
              type="text"
              value={formData.license}
              onChange={(e) => handleFormChange('license', e.target.value)}
              placeholder="MIT"
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
            />
          </div>
        </form>
      </Modal>
    );
  }

  /* ---- main render (populated state) ------------------------------ */

  return (
    <section className="space-y-6 p-6" aria-label="Plugin registry">
      {/* Breadcrumb */}
      <nav aria-label="Breadcrumb" className="text-sm text-gray-500">
        <ol className="flex items-center gap-1.5">
          <li>
            <Link
              to="/"
              className="hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            >
              Home
            </Link>
          </li>
          <li aria-hidden="true" className="select-none">
            /
          </li>
          <li>
            <span className="font-medium text-gray-900" aria-current="page">
              Plugins
            </span>
          </li>
        </ol>
      </nav>

      {/* Page header */}
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Plugins</h1>
          <p className="mt-1 text-sm text-gray-500">
            Manage registered extensions &mdash;{' '}
            <span className="font-medium text-gray-700">
              {plugins.length} {plugins.length === 1 ? 'plugin' : 'plugins'}{' '}
              registered
            </span>
          </p>
        </div>

        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={handleOpenRegister}
            className="inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
          >
            <PlusIcon className="w-4 h-4" />
            Register Plugin
          </button>
          <button
            type="button"
            onClick={() => refetch()}
            className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            title="Refresh plugin list"
            aria-label="Refresh plugin list"
          >
            <RefreshIcon className="w-4 h-4" />
            Refresh
          </button>
        </div>
      </div>

      {/* Data table card */}
      <div className="rounded-lg border border-gray-200 bg-white shadow-sm">
        <DataTable<PluginRow>
          data={plugins as PluginRow[]}
          columns={columns}
          totalCount={plugins.length}
          loading={isLoading}
          hover
          striped
          emptyText="No plugins found matching your criteria."
          responsiveBreakpoint="md"
        />
      </div>

      {/* Modals */}
      {renderRegisterModal()}
      {renderDeleteModal()}
    </section>
  );

  /** Reusable helper: delete confirmation modal JSX. */
  function renderDeleteModal() {
    return (
      <Modal
        isVisible={showDeleteConfirm}
        title="Confirm Deletion"
        size={ModalSize.Small}
        onClose={handleDeleteCancel}
        footer={
          <div className="flex items-center justify-end gap-3">
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
              className="inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {deleteMutation.isPending ? 'Deleting\u2026' : 'Delete'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to unregister plugin{' '}
          <strong className="font-semibold text-gray-900">
            &lsquo;{deleteTarget?.name ?? ''}&rsquo;
          </strong>
          ? This action cannot be undone.
        </p>
        {deleteMutation.isError && (
          <div
            role="alert"
            className="mt-3 rounded-md bg-red-50 p-3 text-sm text-red-700"
          >
            {deleteMutation.error instanceof Error
              ? deleteMutation.error.message
              : 'Failed to delete plugin.'}
          </div>
        )}
      </Modal>
    );
  }
}

export default PluginList;
