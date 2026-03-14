/**
 * CRM Account/Contact TanStack Query Hooks
 *
 * TanStack Query 5 hooks for CRM operations — accounts, contacts, addresses,
 * cross-entity search, and salutation reference data. Replaces the monolith's:
 *
 *  - `NextPlugin.20190204.cs` — Account, contact, address entity definitions
 *                                and relation wiring (account↔contact, contact↔address)
 *  - `NextPlugin.20190206.cs` — Salutation entity and contact↔salutation relation
 *  - `SearchService.cs`       — CRM x_search field indexing (now server-side
 *                                via SNS domain events, not frontend concern)
 *  - `AccountHook.cs`         — Post-create/update search index regeneration
 *                                for account entity (now SNS events)
 *  - `ContactHook.cs`         — Post-create/update search index regeneration
 *                                for contact entity (now SNS events)
 *  - `CrmPlugin.cs`           — CRM plugin entry point
 *
 * Architecture:
 *  - CRM entities (account, contact, address, salutation) are dynamic entities
 *    defined via the entity management system; all use generic {@link EntityRecord}
 *    types rather than static interfaces
 *  - Search indexing (x_search field regeneration from `SearchService.RegenSearchField`)
 *    is handled **server-side** via SNS domain events published after CRUD
 *    operations — the frontend {@link useCrmSearch} hook simply queries the API
 *  - Account↔Contact and Contact↔Address relations are managed via standard
 *    relation payload fields in create/update request bodies
 *  - Salutation is reference data with 30-minute staleTime (rarely changes)
 *
 * Query keys:
 *  - `['accounts', params]`                            — Paginated account list
 *  - `['accounts', id]`                                — Single account
 *  - `['contacts', params]`                            — Paginated contact list
 *  - `['contacts', id]`                                — Single contact
 *  - `['addresses', parentEntityId, parentRecordId]`   — Parent-linked addresses
 *  - `['crm-search', query]`                           — Cross-entity CRM search
 *  - `['salutations']`                                 — Salutation reference data
 *
 * @module hooks/useCrm
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, post, put, del } from '../api/client';
import type {
  EntityRecord,
  EntityRecordList,
  RecordResponse,
  RecordListResponse,
} from '../types/record';
import type { BaseResponseModel, SearchResultList } from '../types/common';

// ---------------------------------------------------------------------------
// Query Keys
// ---------------------------------------------------------------------------

/**
 * Centralised query-key factory for all CRM-domain caches.
 * Prevents key collisions and enables targeted invalidation.
 *
 * Mirrors the monolith's per-entity cache partitioning from `Cache.cs` —
 * accounts, contacts, addresses each have their own cache namespace so
 * that mutations on one entity do not unnecessarily invalidate another.
 */
const CRM_QUERY_KEYS = {
  accounts: {
    /** Root key for all account queries — used for broadest invalidation */
    all: ['accounts'] as const,
    /** Paginated account list with optional filters */
    list: (params?: AccountsParams) => ['accounts', params] as const,
    /** Single account by ID */
    detail: (id: string) => ['accounts', id] as const,
  },
  contacts: {
    /** Root key for all contact queries */
    all: ['contacts'] as const,
    /** Paginated contact list with optional filters */
    list: (params?: ContactsParams) => ['contacts', params] as const,
    /** Single contact by ID */
    detail: (id: string) => ['contacts', id] as const,
  },
  addresses: {
    /** Root key for all address queries */
    all: ['addresses'] as const,
    /** Addresses linked to a specific parent entity record */
    list: (parentEntityId: string, parentRecordId: string) =>
      ['addresses', parentEntityId, parentRecordId] as const,
  },
  crmSearch: {
    /** Root key for all CRM search queries */
    all: ['crm-search'] as const,
    /** Cross-entity CRM search replacing SearchService + x_search querying */
    byQuery: (query: string) => ['crm-search', query] as const,
  },
  salutations: {
    /** Root key for salutation reference data (rarely changes) */
    all: ['salutations'] as const,
  },
} as const;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * staleTime for salutation reference data — 30 minutes (1 800 000 ms).
 *
 * Salutations (Mr, Mrs, Ms, Dr, etc.) are reference data from the
 * `salutation` entity created by `NextPlugin.20190206.cs`. They change
 * extremely rarely, so an aggressive staleTime reduces network calls.
 */
const SALUTATION_STALE_TIME_MS = 30 * 60 * 1000;

/**
 * Default staleTime for CRM entity queries — 30 seconds (30 000 ms).
 *
 * Accounts and contacts are moderately volatile. The monolith served
 * fresh data on every request via direct DB queries in RecordManager.Find().
 * The 30-second staleTime balances network efficiency with data freshness.
 */
const CRM_DEFAULT_STALE_TIME_MS = 30 * 1000;

// ---------------------------------------------------------------------------
// Parameter Interfaces
// ---------------------------------------------------------------------------

/**
 * Query parameters for the {@link useAccounts} hook.
 *
 * Maps to query string parameters accepted by `GET /v1/crm/accounts`.
 * Supports filtering by search text, industry, type, and city — fields
 * defined in the `account` entity by `NextPlugin.20190204.cs`.
 *
 * Replaces the C# EQL-based query against `rec_account` table that
 * the monolith's RecordManager.Find() executed.
 */
export interface AccountsParams {
  /** Free-text search across account name, email, phone (server-side x_search) */
  search?: string;
  /** Filter by industry field value */
  industry?: string;
  /** Filter by account type field value */
  type?: string;
  /** Filter by city field value */
  city?: string;
  /** Page number (1-based) */
  page?: number;
  /** Number of records per page */
  pageSize?: number;
  /** Sort expression (e.g. "name:asc", "created_on:desc") */
  sort?: string;
}

/**
 * Query parameters for the {@link useContacts} hook.
 *
 * Maps to query string parameters accepted by `GET /v1/crm/contacts`.
 * Supports filtering by search text, parent account, and salutation —
 * fields and relations defined by `NextPlugin.20190204.cs` (contact entity,
 * account↔contact relation) and `NextPlugin.20190206.cs` (salutation relation).
 */
export interface ContactsParams {
  /** Free-text search across contact name, email, phone (server-side x_search) */
  search?: string;
  /** Filter by associated account ID (account↔contact relation) */
  accountId?: string;
  /** Filter by salutation ID (contact↔salutation relation from 20190206 patch) */
  salutationId?: string;
  /** Page number (1-based) */
  page?: number;
  /** Number of records per page */
  pageSize?: number;
  /** Sort expression (e.g. "last_name:asc", "created_on:desc") */
  sort?: string;
}

// ---------------------------------------------------------------------------
// Mutation Variable Interfaces
// ---------------------------------------------------------------------------

/**
 * Variables for the {@link useUpdateAccount} mutation.
 *
 * Replaces in-process `RecordManager.UpdateRecord("account", record)` which
 * used merge semantics — only supplied fields are updated; unspecified fields
 * retain their current values. The server handles field normalisation.
 */
interface UpdateAccountVariables {
  /** Account record ID (GUID string) to update */
  id: string;
  /** Partial account data — only changed fields required (merge semantics) */
  data: EntityRecord;
}

/**
 * Variables for the {@link useUpdateContact} mutation.
 *
 * Replaces in-process `RecordManager.UpdateRecord("contact", record)` with
 * HTTP PUT to the CRM microservice. Post-update search indexing (previously
 * handled by `ContactHook.OnPostUpdateRecord` → `SearchService.RegenSearchField`)
 * is now triggered server-side via SNS domain events.
 */
interface UpdateContactVariables {
  /** Contact record ID (GUID string) to update */
  id: string;
  /** Partial contact data — only changed fields required (merge semantics) */
  data: EntityRecord;
}

/**
 * Variables for the {@link useUpdateAddress} mutation.
 *
 * Replaces in-process `RecordManager.UpdateRecord("address", record)` with
 * HTTP PUT. Address records are linked to accounts/contacts via entity relations.
 */
interface UpdateAddressVariables {
  /** Address record ID (GUID string) to update */
  id: string;
  /** Partial address data — only changed fields required (merge semantics) */
  data: EntityRecord;
}

// ---------------------------------------------------------------------------
// Internal Helpers
// ---------------------------------------------------------------------------

/**
 * Validates an API response and throws a descriptive error when the
 * operation failed.
 *
 * Handles two response shapes from the CRM Lambda:
 *  - **Envelope format** (errors): `{ success: false, message, errors }` —
 *    mirrors BaseResponseModel from `ApiControllerBase.cs`
 *  - **Raw format** (success): `{ id, name, ... }` or `{ data: [...] }` —
 *    Lambda returns raw data without an envelope on success
 *
 * When the response has no explicit `success` field it is treated as a
 * successful raw Lambda response.
 *
 * @param response - API response (may or may not include success flag)
 * @param fallbackMessage - Default error message when no specific errors returned
 * @throws Error with concatenated error messages from the response envelope
 */
function assertApiSuccess(
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  response: any,
  fallbackMessage: string,
): void {
  // Raw Lambda responses without envelope — treat as success
  if (typeof response?.success !== 'boolean') {
    return;
  }
  if (!response.success) {
    const errorMessages = (response.errors as Array<{ message?: string }> | undefined)
      ?.map((err) => err.message)
      .filter(Boolean);
    throw new Error(
      errorMessages && errorMessages.length > 0
        ? errorMessages.join('; ')
        : (response.message as string) || fallbackMessage,
    );
  }
}

/**
 * Unwraps the typed payload from a response that may or may not use the
 * `ApiResponse<T>` envelope.
 *
 * - Envelope format:  `{ success, object: T }` → returns `response.object`
 * - Raw format:       `T` directly                → returns `response` itself
 */
function unwrapObject<T>(response: unknown): T {
  const r = response as Record<string, unknown> | undefined;
  return ((r?.object ?? r) as T);
}

/**
 * Normalises a list response into the standard {@link EntityRecordList}
 * shape regardless of whether the Lambda returned the envelope format
 * or a raw `{ data, meta }` shape.
 *
 * CRM Lambda list responses: `{ data: EntityRecord[], meta: { page, pageSize, total } }`
 * Expected frontend shape:   `{ records: EntityRecord[], totalCount: number }`
 */
function unwrapRecordList(response: unknown): EntityRecordList {
  const r = (response as Record<string, unknown> | undefined);
  const raw = (r?.object ?? r) as Record<string, unknown> | undefined;
  const records = (
    (raw?.records ?? raw?.data ?? raw?.items ?? []) as EntityRecord[]
  );
  const meta = raw?.meta as Record<string, number> | undefined;
  const totalCount = Number(
    raw?.totalCount ?? meta?.total ?? raw?.total ?? records.length,
  );
  return { records, totalCount };
}

/**
 * Serialises {@link AccountsParams} into query-string-ready key-value pairs
 * for the `GET /v1/crm/accounts` endpoint.
 *
 * Only includes parameters that have been explicitly set — undefined and
 * empty-string values are omitted to keep the URL clean and allow server
 * defaults to apply.
 */
function buildAccountQueryParams(
  params?: AccountsParams,
): Record<string, unknown> | undefined {
  if (!params) return undefined;

  const queryParams: Record<string, unknown> = {};

  if (params.search !== undefined && params.search !== '') {
    queryParams['search'] = params.search;
  }
  if (params.industry !== undefined && params.industry !== '') {
    queryParams['industry'] = params.industry;
  }
  if (params.type !== undefined && params.type !== '') {
    queryParams['type'] = params.type;
  }
  if (params.city !== undefined && params.city !== '') {
    queryParams['city'] = params.city;
  }
  if (params.page !== undefined) {
    queryParams['page'] = params.page;
  }
  if (params.pageSize !== undefined) {
    queryParams['pageSize'] = params.pageSize;
  }
  if (params.sort !== undefined && params.sort !== '') {
    queryParams['sort'] = params.sort;
  }

  return Object.keys(queryParams).length > 0 ? queryParams : undefined;
}

/**
 * Serialises {@link ContactsParams} into query-string-ready key-value pairs
 * for the `GET /v1/crm/contacts` endpoint.
 */
function buildContactQueryParams(
  params?: ContactsParams,
): Record<string, unknown> | undefined {
  if (!params) return undefined;

  const queryParams: Record<string, unknown> = {};

  if (params.search !== undefined && params.search !== '') {
    queryParams['search'] = params.search;
  }
  if (params.accountId !== undefined && params.accountId !== '') {
    queryParams['accountId'] = params.accountId;
  }
  if (params.salutationId !== undefined && params.salutationId !== '') {
    queryParams['salutationId'] = params.salutationId;
  }
  if (params.page !== undefined) {
    queryParams['page'] = params.page;
  }
  if (params.pageSize !== undefined) {
    queryParams['pageSize'] = params.pageSize;
  }
  if (params.sort !== undefined && params.sort !== '') {
    queryParams['sort'] = params.sort;
  }

  return Object.keys(queryParams).length > 0 ? queryParams : undefined;
}

// ---------------------------------------------------------------------------
// Account Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches a paginated list of CRM accounts with optional filters.
 *
 * Replaces the monolith's `RecordManager.Find("account", query)` which
 * executed an EQL query against `rec_account` table via
 * `DbRecordRepository.Find()`. The monolith's `AccountHook` post-create/
 * post-update hooks regenerated the `x_search` denormalised text field
 * via `SearchService.RegenSearchField(entityName, record, Configuration.AccountSearchIndexFields)`.
 * In the target architecture, search indexing is server-side via SNS events.
 *
 * API: `GET /v1/crm/accounts`
 * Query params: search, industry, type, city, page, pageSize, sort
 * Response shape: `{ success, object: EntityRecordList }`
 *
 * @param params - Optional query parameters for filtering, pagination, and sorting
 * @returns TanStack Query result with `EntityRecordList` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, `refetch`, and `isFetching`
 *
 * @example
 * ```tsx
 * function AccountList() {
 *   const { data, isLoading, isError, error, refetch, isFetching } = useAccounts({
 *     search: 'acme',
 *     industry: 'technology',
 *     page: 1,
 *     pageSize: 25,
 *     sort: 'name:asc',
 *   });
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <DataTable records={data?.records} totalCount={data?.totalCount} />;
 * }
 * ```
 */
export function useAccounts(params?: AccountsParams) {
  return useQuery<RecordListResponse['object'], Error>({
    queryKey: CRM_QUERY_KEYS.accounts.list(params),

    queryFn: async (): Promise<RecordListResponse['object']> => {
      const response = await get<EntityRecordList>(
        '/crm/accounts',
        buildAccountQueryParams(params),
      );
      assertApiSuccess(response, 'Failed to fetch accounts');

      return unwrapRecordList(response);
    },

    staleTime: CRM_DEFAULT_STALE_TIME_MS,
  });
}

/**
 * Fetches a single CRM account by ID.
 *
 * Replaces `RecordManager.Find("account", query)` invoked with an ID
 * equality filter. The CRM microservice exposes a dedicated
 * `/accounts/{id}` endpoint for single-record lookups.
 *
 * API: `GET /v1/crm/accounts/{id}`
 * Response shape: `{ success, object: EntityRecord }`
 *
 * @param id - Account record ID (GUID string)
 * @returns TanStack Query result with `EntityRecord` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, and `refetch`
 *
 * @example
 * ```tsx
 * function AccountDetail({ id }: { id: string }) {
 *   const { data: account, isLoading, isError, error, refetch } = useAccount(id);
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <AccountView record={account} />;
 * }
 * ```
 */
export function useAccount(id: string) {
  return useQuery<RecordResponse['object'], Error>({
    queryKey: CRM_QUERY_KEYS.accounts.detail(id),

    queryFn: async (): Promise<RecordResponse['object']> => {
      const response = await get<EntityRecord>(
        `/crm/accounts/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(response, `Failed to fetch account "${id}"`);

      return unwrapObject<EntityRecord>(response);
    },

    staleTime: CRM_DEFAULT_STALE_TIME_MS,

    // Only fetch when id is provided — prevents unnecessary requests
    // when component mounts before route params resolve
    enabled: id.length > 0,
  });
}

/**
 * Creates a new CRM account record.
 *
 * Replaces `RecordManager.CreateRecord("account", record)` which:
 *  1. Validated entity metadata and field types
 *  2. Normalised field values (ExtractFieldValue per type)
 *  3. Persisted via DbRecordRepository
 *  4. Triggered `AccountHook.OnPostCreateRecord` → `SearchService.RegenSearchField`
 *
 * In the target architecture, post-create search indexing is handled
 * server-side via SNS `crm.account.created` domain event.
 *
 * API: `POST /v1/crm/accounts`
 * Body: {@link EntityRecord} with account field values
 * Response shape: `{ success, object: EntityRecord }` (created account)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function CreateAccountForm() {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useCreateAccount();
 *   const handleSubmit = (formData: EntityRecord) => {
 *     mutate(formData);
 *   };
 * }
 * ```
 */
export function useCreateAccount() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, EntityRecord>({
    mutationFn: async (data: EntityRecord): Promise<RecordResponse['object']> => {
      const response = await post<EntityRecord>('/crm/accounts', data);
      assertApiSuccess(response, 'Failed to create account');

      return unwrapObject<EntityRecord>(response);
    },

    onSuccess: () => {
      // Invalidate all account list queries so the new account appears
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.accounts.all,
      });
    },
  });
}

/**
 * Updates an existing CRM account record with partial data (merge semantics).
 *
 * Replaces `RecordManager.UpdateRecord("account", record)` which used merge
 * semantics — only supplied fields are updated. The monolith's
 * `AccountHook.OnPostUpdateRecord` triggered `SearchService.RegenSearchField`
 * to rebuild the `x_search` denormalised field using
 * `Configuration.AccountSearchIndexFields`. In the target architecture,
 * this indexing is handled by the CRM service via SNS `crm.account.updated` event.
 *
 * API: `PUT /v1/crm/accounts/{id}`
 * Body: partial {@link EntityRecord}
 * Response shape: `{ success, object: EntityRecord }` (updated account)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function EditAccountForm({ accountId }: { accountId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useUpdateAccount();
 *   const handleSubmit = (changes: EntityRecord) => {
 *     mutate({ id: accountId, data: changes });
 *   };
 * }
 * ```
 */
export function useUpdateAccount() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, UpdateAccountVariables>({
    mutationFn: async ({
      id,
      data,
    }: UpdateAccountVariables): Promise<RecordResponse['object']> => {
      const response = await put<EntityRecord>(
        `/crm/accounts/${encodeURIComponent(id)}`,
        data,
      );
      assertApiSuccess(response, `Failed to update account "${id}"`);

      return unwrapObject<EntityRecord>(response);
    },

    onSuccess: (_data, variables) => {
      // Invalidate all account list queries to reflect the update
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.accounts.all,
      });
      // Invalidate the specific account detail cache
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.accounts.detail(variables.id),
      });
    },
  });
}

/**
 * Deletes a CRM account record by ID.
 *
 * Replaces `RecordManager.DeleteRecord("account", recordId)` which:
 *  1. Enforced EntityPermission.CanDelete
 *  2. Executed pre-delete hooks
 *  3. Cleaned up file fields and related records
 *  4. Deleted the record from `rec_account` table
 *  5. Executed post-delete hooks → SNS domain events
 *
 * API: `DELETE /v1/crm/accounts/{id}`
 * Response: success envelope only (no typed object)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, and `reset`
 *
 * @example
 * ```tsx
 * function DeleteAccountButton({ accountId }: { accountId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, reset } = useDeleteAccount();
 *   return (
 *     <button onClick={() => mutate(accountId)} disabled={isPending}>
 *       Delete Account
 *     </button>
 *   );
 * }
 * ```
 */
export function useDeleteAccount() {
  const queryClient = useQueryClient();

  return useMutation<void, Error, string>({
    mutationFn: async (id: string): Promise<void> => {
      const response = await del(
        `/crm/accounts/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(response, `Failed to delete account "${id}"`);
    },

    onSuccess: () => {
      // Invalidate all account queries — the deleted account must
      // disappear from lists and detail caches
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.accounts.all,
      });
      // Also invalidate contacts (account deletion may cascade)
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.contacts.all,
      });
    },
  });
}

// ---------------------------------------------------------------------------
// Contact Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches a paginated list of CRM contacts with optional filters.
 *
 * Replaces the monolith's `RecordManager.Find("contact", query)` which
 * queried the `rec_contact` table via EQL. The monolith's
 * `ContactHook.OnPostCreateRecord` / `OnPostUpdateRecord` regenerated the
 * `x_search` denormalised text field via `SearchService.RegenSearchField(
 * "contact", record, Configuration.ContactSearchIndexFields)`. In the target
 * architecture, search indexing is server-side via SNS events.
 *
 * Filter parameters map to the fields defined in `NextPlugin.20190204.cs`
 * (first_name, last_name, email, phone, company, job_title) and the
 * `NextPlugin.20190206.cs` salutation relation.
 *
 * API: `GET /v1/crm/contacts`
 * Query params: search, accountId, salutationId, page, pageSize, sort
 * Response shape: `{ success, object: EntityRecordList }`
 *
 * @param params - Optional query parameters for filtering, pagination, and sorting
 * @returns TanStack Query result with `EntityRecordList` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, `refetch`, and `isFetching`
 *
 * @example
 * ```tsx
 * function ContactList({ accountId }: { accountId?: string }) {
 *   const { data, isLoading, isError, error, refetch, isFetching } = useContacts({
 *     accountId,
 *     page: 1,
 *     pageSize: 25,
 *     sort: 'last_name:asc',
 *   });
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <DataTable records={data?.records} totalCount={data?.totalCount} />;
 * }
 * ```
 */
export function useContacts(params?: ContactsParams) {
  return useQuery<RecordListResponse['object'], Error>({
    queryKey: CRM_QUERY_KEYS.contacts.list(params),

    queryFn: async (): Promise<RecordListResponse['object']> => {
      const response = await get<EntityRecordList>(
        '/crm/contacts',
        buildContactQueryParams(params),
      );
      assertApiSuccess(response, 'Failed to fetch contacts');

      return unwrapRecordList(response);
    },

    staleTime: CRM_DEFAULT_STALE_TIME_MS,
  });
}

/**
 * Fetches a single CRM contact by ID.
 *
 * Replaces `RecordManager.Find("contact", query)` invoked with an ID
 * equality filter. The CRM microservice exposes a dedicated
 * `/contacts/{id}` endpoint for single-record lookups.
 *
 * API: `GET /v1/crm/contacts/{id}`
 * Response shape: `{ success, object: EntityRecord }`
 *
 * @param id - Contact record ID (GUID string)
 * @returns TanStack Query result with `EntityRecord` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, and `refetch`
 *
 * @example
 * ```tsx
 * function ContactDetail({ id }: { id: string }) {
 *   const { data: contact, isLoading, isError, error, refetch } = useContact(id);
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <ContactView record={contact} />;
 * }
 * ```
 */
export function useContact(id: string) {
  return useQuery<RecordResponse['object'], Error>({
    queryKey: CRM_QUERY_KEYS.contacts.detail(id),

    queryFn: async (): Promise<RecordResponse['object']> => {
      const response = await get<EntityRecord>(
        `/crm/contacts/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(response, `Failed to fetch contact "${id}"`);

      return unwrapObject<EntityRecord>(response);
    },

    staleTime: CRM_DEFAULT_STALE_TIME_MS,

    // Only fetch when id is provided
    enabled: id.length > 0,
  });
}

/**
 * Creates a new CRM contact record.
 *
 * Replaces `RecordManager.CreateRecord("contact", record)` which:
 *  1. Validated entity metadata and field types
 *  2. Normalised field values (first_name, last_name, email, phone, etc.)
 *  3. Persisted via DbRecordRepository into `rec_contact` table
 *  4. Triggered `ContactHook.OnPostCreateRecord` → `SearchService.RegenSearchField`
 *
 * The contact entity was originally created by `NextPlugin.20190204.cs`
 * with fields: first_name, last_name, email, phone, company, job_title.
 * Salutation was added by `NextPlugin.20190206.cs` as a relation.
 *
 * In the target architecture, post-create search indexing is handled
 * server-side via SNS `crm.contact.created` domain event.
 *
 * API: `POST /v1/crm/contacts`
 * Body: {@link EntityRecord} with contact field values
 * Response shape: `{ success, object: EntityRecord }` (created contact)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function CreateContactForm() {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useCreateContact();
 *   const handleSubmit = (formData: EntityRecord) => {
 *     mutate(formData);
 *   };
 * }
 * ```
 */
export function useCreateContact() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, EntityRecord>({
    mutationFn: async (data: EntityRecord): Promise<RecordResponse['object']> => {
      const response = await post<EntityRecord>('/crm/contacts', data);
      assertApiSuccess(response, 'Failed to create contact');

      return unwrapObject<EntityRecord>(response);
    },

    onSuccess: () => {
      // Invalidate all contact list queries so the new contact appears
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.contacts.all,
      });
    },
  });
}

/**
 * Updates an existing CRM contact record with partial data (merge semantics).
 *
 * Replaces `RecordManager.UpdateRecord("contact", record)` which used merge
 * semantics. The monolith's `ContactHook.OnPostUpdateRecord` triggered
 * `SearchService.RegenSearchField` using
 * `Configuration.ContactSearchIndexFields`. In the target architecture,
 * indexing is via SNS `crm.contact.updated` event.
 *
 * API: `PUT /v1/crm/contacts/{id}`
 * Body: partial {@link EntityRecord}
 * Response shape: `{ success, object: EntityRecord }` (updated contact)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function EditContactForm({ contactId }: { contactId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useUpdateContact();
 *   const handleSubmit = (changes: EntityRecord) => {
 *     mutate({ id: contactId, data: changes });
 *   };
 * }
 * ```
 */
export function useUpdateContact() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, UpdateContactVariables>({
    mutationFn: async ({
      id,
      data,
    }: UpdateContactVariables): Promise<RecordResponse['object']> => {
      const response = await put<EntityRecord>(
        `/crm/contacts/${encodeURIComponent(id)}`,
        data,
      );
      assertApiSuccess(response, `Failed to update contact "${id}"`);

      return unwrapObject<EntityRecord>(response);
    },

    onSuccess: (_data, variables) => {
      // Invalidate all contact list queries to reflect the update
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.contacts.all,
      });
      // Invalidate the specific contact detail cache
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.contacts.detail(variables.id),
      });
    },
  });
}

/**
 * Deletes a CRM contact record by ID.
 *
 * Replaces `RecordManager.DeleteRecord("contact", recordId)` which
 * enforced EntityPermission.CanDelete, executed pre/post hooks,
 * and cleaned up related records.
 *
 * API: `DELETE /v1/crm/contacts/{id}`
 * Response: success envelope only (no typed object)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, and `reset`
 *
 * @example
 * ```tsx
 * function DeleteContactButton({ contactId }: { contactId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, reset } = useDeleteContact();
 *   return (
 *     <button onClick={() => mutate(contactId)} disabled={isPending}>
 *       Delete Contact
 *     </button>
 *   );
 * }
 * ```
 */
export function useDeleteContact() {
  const queryClient = useQueryClient();

  return useMutation<void, Error, string>({
    mutationFn: async (id: string): Promise<void> => {
      const response = await del(
        `/crm/contacts/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(response, `Failed to delete contact "${id}"`);
    },

    onSuccess: () => {
      // Invalidate all contact queries — the deleted contact must
      // disappear from lists and detail caches
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.contacts.all,
      });
    },
  });
}

// ---------------------------------------------------------------------------
// Address Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches addresses linked to a parent entity record (account or contact).
 *
 * Replaces EQL queries with `$relation` navigation that the monolith used
 * to join `rec_address` to `rec_account` or `rec_contact` via
 * `entity_relations`. The address entity was originally created in
 * `NextPlugin.20190204.cs` with standard fields (street1, street2, city,
 * state, zip, country, type) and relations account↔address, contact↔address.
 *
 * API: `GET /v1/crm/addresses?parentEntityId={entityId}&parentRecordId={recordId}`
 * Response shape: `{ success, object: EntityRecordList }`
 *
 * @param parentEntityId - Parent entity identifier (e.g., 'account' or 'contact')
 * @param parentRecordId - Parent record ID (GUID string) to scope addresses
 * @returns TanStack Query result with `EntityRecordList` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, `refetch`, and `isFetching`
 *
 * @example
 * ```tsx
 * function AccountAddresses({ accountId }: { accountId: string }) {
 *   const { data, isLoading, isError, error, refetch, isFetching } = useAddresses(
 *     'account',
 *     accountId,
 *   );
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <AddressList addresses={data?.records ?? []} />;
 * }
 * ```
 */
export function useAddresses(parentEntityId: string, parentRecordId: string) {
  return useQuery<RecordListResponse['object'], Error>({
    queryKey: CRM_QUERY_KEYS.addresses.list(parentEntityId, parentRecordId),

    queryFn: async (): Promise<RecordListResponse['object']> => {
      const qp: Record<string, unknown> = {
        parentEntityId,
        parentRecordId,
      };

      const response = await get<EntityRecordList>(
        '/crm/addresses',
        qp,
      );
      assertApiSuccess(response, 'Failed to fetch addresses');

      return unwrapRecordList(response);
    },

    staleTime: CRM_DEFAULT_STALE_TIME_MS,

    // Only fetch when both parent identifiers are provided
    enabled: parentEntityId.length > 0 && parentRecordId.length > 0,
  });
}

/**
 * Creates a new CRM address record linked to a parent entity (account or contact).
 *
 * The address entity is linked to accounts/contacts via entity relations.
 * The `EntityRecord` body should include `parentEntityId` and
 * `parentRecordId` (or equivalent relation fields) so the CRM service
 * can establish the link.
 *
 * API: `POST /v1/crm/addresses`
 * Body: {@link EntityRecord} with address field values + parent relation
 * Response shape: `{ success, object: EntityRecord }` (created address)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function CreateAddressForm({ parentEntityId, parentRecordId }: Props) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useCreateAddress();
 *   const handleSubmit = (formData: EntityRecord) => {
 *     mutate({ ...formData, parentEntityId, parentRecordId });
 *   };
 * }
 * ```
 */
export function useCreateAddress() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, EntityRecord>({
    mutationFn: async (data: EntityRecord): Promise<RecordResponse['object']> => {
      const response = await post<EntityRecord>('/crm/addresses', data);
      assertApiSuccess(response, 'Failed to create address');

      return unwrapObject<EntityRecord>(response);
    },

    onSuccess: () => {
      // Invalidate all address queries — we invalidate the full prefix
      // because the new address could appear in any parent's list
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.addresses.all,
      });
    },
  });
}

/**
 * Updates an existing CRM address record with partial data (merge semantics).
 *
 * API: `PUT /v1/crm/addresses/{id}`
 * Body: partial {@link EntityRecord}
 * Response shape: `{ success, object: EntityRecord }` (updated address)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function EditAddressForm({ addressId }: { addressId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useUpdateAddress();
 *   const handleSubmit = (changes: EntityRecord) => {
 *     mutate({ id: addressId, data: changes });
 *   };
 * }
 * ```
 */
export function useUpdateAddress() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, UpdateAddressVariables>({
    mutationFn: async ({
      id,
      data,
    }: UpdateAddressVariables): Promise<RecordResponse['object']> => {
      const response = await put<EntityRecord>(
        `/crm/addresses/${encodeURIComponent(id)}`,
        data,
      );
      assertApiSuccess(response, `Failed to update address "${id}"`);

      return unwrapObject<EntityRecord>(response);
    },

    onSuccess: () => {
      // Invalidate all address queries — updated address may affect
      // any parent entity's address list
      void queryClient.invalidateQueries({
        queryKey: CRM_QUERY_KEYS.addresses.all,
      });
    },
  });
}

// ---------------------------------------------------------------------------
// CRM Cross-Entity Search Hook
// ---------------------------------------------------------------------------

/**
 * Searches across CRM entities (accounts, contacts, addresses) using a
 * unified search query.
 *
 * Replaces the monolith's `SearchService.RegenSearchField` + x_search
 * querying pattern where each CRM entity had a denormalised `x_search`
 * text field that was rebuilt on every create/update via post-CRUD hooks.
 * The monolith's `SearchManager.Search(query)` then ran a PostgreSQL
 * full-text search against these concatenated `x_search` values.
 *
 * In the target architecture, the CRM microservice handles search indexing
 * via SNS events and exposes a unified `/search` endpoint.
 *
 * API: `GET /v1/crm/search?q={query}`
 * Response shape: `{ success, object: SearchResultList }`
 *
 * @param query - Search query string; the component layer should debounce
 *                this value at ~300ms before passing to the hook
 * @returns TanStack Query result with `SearchResultList` data, plus
 *          `isLoading`, `isError`, `error`, `isSuccess`, `refetch`, and `isFetching`
 *
 * @example
 * ```tsx
 * function CrmSearchBar() {
 *   const [query, setQuery] = useState('');
 *   const debouncedQuery = useDebouncedValue(query, 300);
 *   const { data, isLoading, isError, error, refetch, isFetching } = useCrmSearch(debouncedQuery);
 *   return (
 *     <div>
 *       <input value={query} onChange={(e) => setQuery(e.target.value)} />
 *       {isLoading && <Spinner />}
 *       {data?.results.map((result) => (
 *         <SearchResultCard key={result.id} result={result} />
 *       ))}
 *     </div>
 *   );
 * }
 * ```
 */
export function useCrmSearch(query: string) {
  return useQuery<SearchResultList, Error>({
    queryKey: CRM_QUERY_KEYS.crmSearch.byQuery(query),

    queryFn: async (): Promise<SearchResultList> => {
      const response = await get<SearchResultList>(
        '/crm/search',
        { q: query },
      );
      assertApiSuccess(response, 'CRM search failed');

      return unwrapObject<SearchResultList>(response);
    },

    staleTime: CRM_DEFAULT_STALE_TIME_MS,

    // Only execute when query is non-empty (debounce at component level)
    enabled: query.trim().length > 0,
  });
}

// ---------------------------------------------------------------------------
// Salutation Reference Data Hook
// ---------------------------------------------------------------------------

/**
 * Fetches the list of available salutations (Mr, Mrs, Ms, Dr, etc.).
 *
 * Replaces querying the `salutation` entity created by
 * `NextPlugin.20190206.cs`. The salutation entity was introduced to
 * provide a lookup list for the contact→salutation relation. Since
 * salutations are reference data that rarely changes, this query uses
 * a 30-minute stale time.
 *
 * API: `GET /v1/crm/salutations`
 * Response shape: `{ success, object: EntityRecordList }`
 *
 * @returns TanStack Query result with `EntityRecordList` data, plus
 *          `isLoading`, `isError`, `error`, `isSuccess`, and `refetch`
 *
 * @example
 * ```tsx
 * function SalutationSelect({ value, onChange }: SelectProps) {
 *   const { data, isLoading, isError, error, refetch } = useSalutations();
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return (
 *     <select value={value} onChange={onChange}>
 *       <option value="">Select salutation…</option>
 *       {data?.records.map((s) => (
 *         <option key={String(s.id)} value={String(s.id)}>
 *           {String(s.name ?? '')}
 *         </option>
 *       ))}
 *     </select>
 *   );
 * }
 * ```
 */
export function useSalutations() {
  return useQuery<RecordListResponse['object'], Error>({
    queryKey: CRM_QUERY_KEYS.salutations.all,

    queryFn: async (): Promise<RecordListResponse['object']> => {
      const response = await get<EntityRecordList>('/crm/salutations');
      assertApiSuccess(response, 'Failed to fetch salutations');

      return unwrapRecordList(response);
    },

    // Salutations are reference data that rarely changes — use long stale time
    staleTime: SALUTATION_STALE_TIME_MS,
  });
}
