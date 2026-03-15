/**
 * SmtpServiceList.tsx — SMTP Service Configuration Listing
 *
 * React page component that displays all SMTP service configurations in a
 * sortable data table.  Replaces the Mail plugin's "SMTP Services" list page
 * originally created by `MailPlugin.20190215.cs` (under the `services` sitemap
 * area) and the corresponding PcGrid ViewComponent.
 *
 * Features:
 *  - Fetches SMTP services via GET /notifications/smtp-services (TanStack Query)
 *  - Renders a DataTable with columns for name, server, port, default flag,
 *    enabled status, connection security, sender email, and row actions
 *  - Delete confirmation via Modal + DELETE mutation with cache invalidation
 *  - "Add Service" button navigating to the create page
 *  - Row-level "Edit" action navigating to the manage page
 *  - Connection security numeric values mapped to human-readable labels
 *  - Default service rows visually highlighted
 *  - Loading, error, and empty states handled gracefully
 */

import React, { useState, useMemo, useCallback } from 'react';
import {
  useQuery,
  useMutation,
  useQueryClient,
} from '@tanstack/react-query';
import { useNavigate, Link } from 'react-router-dom';
import apiClient, { del } from '../../api/client';
import type { ApiResponse } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Modal from '../../components/common/Modal';

/* ------------------------------------------------------------------ */
/*  Types                                                              */
/* ------------------------------------------------------------------ */

/**
 * Represents a single SMTP service configuration record.
 *
 * Field definitions are derived from the `smtp_service` entity created in
 * `MailPlugin.20190215.cs` (lines 580-1021).  Property names use snake_case
 * to match the JSON serialisation produced by the Notifications service.
 */
interface SmtpService {
  /** Index signature required by DataTable<T extends Record<string, unknown>> */
  [key: string]: unknown;
  /** Unique identifier (GUID) */
  id: string;
  /** Display name — required, unique, max 100 chars */
  name: string;
  /** SMTP server hostname or IP address */
  server: string;
  /** SMTP port — 1-65 535, default 25 */
  port: number;
  /** SMTP authentication username (nullable) */
  username: string | null;
  /** SMTP authentication password (nullable) */
  password: string | null;
  /** Default sender display name */
  default_sender_name: string;
  /** Default "From" email address — required */
  default_sender_email: string;
  /** Default "Reply-To" email address (nullable) */
  default_reply_to_email: string | null;
  /** Maximum retry attempts for failed sends — 0-10, default 3 */
  max_retries_count: number;
  /** Minutes to wait between retries — 0-1440, default 60 */
  retry_wait_minutes: number;
  /** Whether this is the default outbound service */
  is_default: boolean;
  /** Whether the service is currently enabled */
  is_enabled: boolean;
  /**
   * Connection security mode — stored as a string-encoded integer.
   * Possible values: '0' (None) | '1' (Auto) | '2' (SslOnConnect) |
   *                  '3' (StartTls) | '4' (StartTlsWhenAvailable)
   */
  connection_security: string;
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Base API path for SMTP service endpoints (apiClient baseURL already includes /v1) */
const SMTP_BASE = '/notifications/smtp-services';

/**
 * Human-readable labels for the `connection_security` select-field options.
 * Values mirror the `SecureSocketOptions` enum items registered in
 * `MailPlugin.20190215.cs` (lines 912-918).
 */
const CONNECTION_SECURITY_LABELS: Record<string, string> = {
  '0': 'None',
  '1': 'Auto',
  '2': 'SSL on Connect',
  '3': 'StartTLS',
  '4': 'StartTLS When Available',
};

/* ------------------------------------------------------------------ */
/*  Component                                                          */
/* ------------------------------------------------------------------ */

/**
 * SMTP Service list page — default export, lazy-loadable via React Router.
 *
 * Renders a page header with an "Add Service" CTA and a DataTable listing
 * all configured SMTP services.  Each row exposes edit / delete actions.
 */
export default function SmtpServiceList(): React.JSX.Element {
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ---- Delete-confirmation modal state ---- */
  const [deleteTarget, setDeleteTarget] = useState<SmtpService | null>(null);
  const [isDeleteModalVisible, setIsDeleteModalVisible] = useState(false);

  /* ---- Data fetching ---- */
  const {
    data: services,
    isLoading,
    isError,
  } = useQuery<SmtpService[]>({
    queryKey: ['smtp-services'],
    queryFn: async (): Promise<SmtpService[]> => {
      const response = await apiClient.get<ApiResponse<SmtpService[]>>(
        SMTP_BASE,
      );
      return response.data.object ?? [];
    },
  });

  /* ---- Delete mutation ---- */
  const deleteMutation = useMutation<ApiResponse, Error, string>({
    mutationFn: (id: string) => del(`${SMTP_BASE}/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['smtp-services'] });
      setIsDeleteModalVisible(false);
      setDeleteTarget(null);
    },
  });

  /* ---- Navigation handlers ---- */
  const handleAddService = useCallback(() => {
    navigate('/notifications/smtp/create');
  }, [navigate]);

  const handleEditClick = useCallback(
    (service: SmtpService) => {
      navigate(`/notifications/smtp/${service.id}/manage`);
    },
    [navigate],
  );

  /* ---- Delete handlers ---- */
  const handleDeleteClick = useCallback((service: SmtpService) => {
    setDeleteTarget(service);
    setIsDeleteModalVisible(true);
  }, []);

  const handleConfirmDelete = useCallback(() => {
    if (deleteTarget) {
      deleteMutation.mutate(deleteTarget.id);
    }
  }, [deleteTarget, deleteMutation]);

  const handleCancelDelete = useCallback(() => {
    setIsDeleteModalVisible(false);
    setDeleteTarget(null);
  }, []);

  /* ---- Column definitions ---- */
  const columns = useMemo(
    (): DataTableColumn<SmtpService>[] => [
      {
        id: 'name',
        label: 'Name',
        accessorKey: 'name',
        sortable: true,
        cell: (_value: unknown, record: SmtpService) => (
          <Link
            to={`/notifications/smtp/${record.id}/manage`}
            className="font-medium text-blue-600 hover:text-blue-800 hover:underline"
          >
            {record.name}
          </Link>
        ),
      },
      {
        id: 'server',
        label: 'Server',
        accessorKey: 'server',
        sortable: true,
      },
      {
        id: 'port',
        label: 'Port',
        accessorKey: 'port',
        sortable: true,
        cell: (value: unknown) => (
          <span className="font-mono text-sm tabular-nums">
            {String(value ?? '')}
          </span>
        ),
      },
      {
        id: 'is_default',
        label: 'Default',
        accessorKey: 'is_default',
        sortable: true,
        horizontalAlign: 'center',
        cell: (value: unknown) =>
          value === true ? (
            <span
              className="inline-flex items-center justify-center text-green-600"
              aria-label="Default service"
              role="img"
            >
              {/* Checkmark SVG — inline, monochrome */}
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-5 w-5"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"
                  clipRule="evenodd"
                />
              </svg>
            </span>
          ) : null,
      },
      {
        id: 'is_enabled',
        label: 'Enabled',
        accessorKey: 'is_enabled',
        sortable: true,
        horizontalAlign: 'center',
        cell: (value: unknown) =>
          value === true ? (
            <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">
              Active
            </span>
          ) : (
            <span className="inline-flex items-center rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-medium text-red-800">
              Disabled
            </span>
          ),
      },
      {
        id: 'connection_security',
        label: 'Security',
        accessorKey: 'connection_security',
        sortable: true,
        cell: (value: unknown) => (
          <span className="whitespace-nowrap text-sm text-gray-700">
            {CONNECTION_SECURITY_LABELS[String(value ?? '')] ??
              String(value ?? '')}
          </span>
        ),
      },
      {
        id: 'default_sender_email',
        label: 'Sender',
        accessorKey: 'default_sender_email',
        sortable: true,
        cell: (value: unknown) => (
          <span className="text-sm text-gray-700">
            {String(value ?? '')}
          </span>
        ),
      },
      {
        id: 'actions',
        label: 'Actions',
        horizontalAlign: 'right',
        cell: (_value: unknown, record: SmtpService) => (
          <div className="flex items-center justify-end gap-2">
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                handleEditClick(record);
              }}
              className="inline-flex items-center rounded px-2.5 py-1.5 text-xs font-medium text-blue-700 hover:bg-blue-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              aria-label={`Edit ${record.name}`}
            >
              Edit
            </button>
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                handleDeleteClick(record);
              }}
              className="inline-flex items-center rounded px-2.5 py-1.5 text-xs font-medium text-red-700 hover:bg-red-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
              aria-label={`Delete ${record.name}`}
            >
              Delete
            </button>
          </div>
        ),
      },
    ],
    [handleEditClick, handleDeleteClick],
  );

  /* ---- Loading state ---- */
  if (isLoading) {
    return (
      <div className="flex min-h-[16rem] items-center justify-center">
        <div className="flex flex-col items-center gap-3">
          {/* Spinner */}
          <svg
            className="h-8 w-8 animate-spin text-blue-600"
            xmlns="http://www.w3.org/2000/svg"
            fill="none"
            viewBox="0 0 24 24"
            aria-hidden="true"
          >
            <circle
              className="opacity-25"
              cx="12"
              cy="12"
              r="10"
              stroke="currentColor"
              strokeWidth="4"
            />
            <path
              className="opacity-75"
              fill="currentColor"
              d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
            />
          </svg>
          <p className="text-sm text-gray-500">
            Loading SMTP services&hellip;
          </p>
        </div>
      </div>
    );
  }

  /* ---- Error state ---- */
  if (isError) {
    return (
      <div className="flex min-h-[16rem] items-center justify-center">
        <div className="rounded-lg border border-red-200 bg-red-50 px-6 py-5 text-center">
          <p className="text-sm font-medium text-red-800">
            Failed to load SMTP services.
          </p>
          <p className="mt-1 text-xs text-red-600">
            Please try refreshing the page or contact your administrator.
          </p>
        </div>
      </div>
    );
  }

  /* ---- Resolved data ---- */
  const smtpServices: SmtpService[] = services ?? [];

  /* ---- Main render ---- */
  return (
    <section className="space-y-6">
      {/* ---- Page header ---- */}
      <header className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold text-gray-900">
            SMTP Services
          </h1>
          <p className="mt-1 text-sm text-gray-500">
            Manage outbound email service configurations.
          </p>
        </div>
        <button
          type="button"
          onClick={handleAddService}
          className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          {/* Plus icon */}
          <svg
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 20 20"
            fill="currentColor"
            className="h-4 w-4"
            aria-hidden="true"
          >
            <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
          </svg>
          Add Service
        </button>
      </header>

      {/* ---- Data table ---- */}
      <DataTable<SmtpService>
        data={smtpServices}
        columns={columns}
        loading={isLoading}
        hover
        striped
        emptyText="No SMTP services configured. Click &ldquo;Add Service&rdquo; to create one."
        name="smtp-services"
      />

      {/* ---- Delete confirmation modal ---- */}
      <Modal
        isVisible={isDeleteModalVisible}
        onClose={handleCancelDelete}
        title="Confirm Deletion"
        id="smtp-delete-modal"
      >
        <div className="space-y-4">
          <p className="text-sm text-gray-700">
            Are you sure you want to delete the SMTP service{' '}
            <strong className="font-semibold text-gray-900">
              {deleteTarget?.name ?? ''}
            </strong>
            ? This action cannot be undone.
          </p>

          {/* Modal action buttons */}
          <div className="flex items-center justify-end gap-3 border-t border-gray-200 pt-4">
            <button
              type="button"
              onClick={handleCancelDelete}
              disabled={deleteMutation.isPending}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmDelete}
              disabled={deleteMutation.isPending}
              className="inline-flex items-center rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {deleteMutation.isPending ? 'Deleting\u2026' : 'Delete'}
            </button>
          </div>
        </div>
      </Modal>
    </section>
  );
}
