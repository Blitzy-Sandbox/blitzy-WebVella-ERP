/**
 * CRM Account/Contact Operations API Module
 *
 * Typed API functions for CRM account and contact management that route to the
 * CRM bounded-context service via API Gateway. Replaces the monolith's direct
 * RecordManager invocations for CRM entities (account, contact) that were
 * originally created in NextPlugin.20190204.cs and NextPlugin.20190206.cs.
 *
 * Account entity fields: name, industry, fax, phone, email, website, address,
 *   notes, x_search, type (Company/Person), region, post_code, fixed_phone,
 *   mobile_phone, fax_phone, created_on
 *
 * Contact entity fields: first_name, last_name, email, phone, address, notes,
 *   x_search, created_on
 *
 * The x_search composite field is maintained server-side by the CRM service
 * (replacing the monolith's SearchService.RegenSearchField hook-based indexing).
 */

import { get, post, put, del } from '../client';
import type { ApiResponse } from '../client';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Query parameters shared across account and contact list/search operations.
 * Maps to the pagination, sorting, and filtering capabilities formerly handled
 * by the monolith's EQL WHERE/ORDER/PAGE clauses.
 */
export interface CrmListParams {
  /** Free-text search against the x_search composite field */
  search?: string;
  /** 1-based page number for pagination (default: 1) */
  page?: number;
  /** Number of records per page (default determined by server) */
  pageSize?: number;
  /** Entity field name to sort by (e.g. 'name', 'created_on') */
  sortField?: string;
  /** Sort direction — ascending or descending */
  sortType?: 'asc' | 'desc';
  /** Additional field-level equality/range filters */
  filters?: Record<string, unknown>;
}

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/**
 * Serialises CrmListParams into a flat Record suitable for query-string
 * encoding via the client `get` helper. The `filters` property is JSON-
 * stringified so the API Gateway can deserialise it server-side.
 */
function buildListParams(
  params?: CrmListParams,
): Record<string, unknown> | undefined {
  if (!params) {
    return undefined;
  }

  const query: Record<string, unknown> = {};

  if (params.search !== undefined && params.search !== '') {
    query['search'] = params.search;
  }
  if (params.page !== undefined) {
    query['page'] = params.page;
  }
  if (params.pageSize !== undefined) {
    query['pageSize'] = params.pageSize;
  }
  if (params.sortField !== undefined && params.sortField !== '') {
    query['sortField'] = params.sortField;
  }
  if (params.sortType !== undefined) {
    query['sortType'] = params.sortType;
  }
  if (params.filters !== undefined && Object.keys(params.filters).length > 0) {
    query['filters'] = JSON.stringify(params.filters);
  }

  return Object.keys(query).length > 0 ? query : undefined;
}

// ---------------------------------------------------------------------------
// Account endpoints — /crm/accounts
// ---------------------------------------------------------------------------

/**
 * List account records with optional pagination, search, and filtering.
 *
 * GET /crm/accounts
 *
 * @param params - Optional query/pagination/sort/filter parameters
 * @returns Paginated array of account EntityRecord objects
 */
export function listAccounts(
  params?: CrmListParams,
): Promise<ApiResponse<EntityRecord[]>> {
  return get<EntityRecord[]>('/crm/accounts', buildListParams(params));
}

/**
 * Retrieve a single account record by its unique identifier.
 *
 * GET /crm/accounts/{accountId}
 *
 * @param accountId - UUID of the account to retrieve
 * @returns The matching account EntityRecord
 */
export function getAccount(
  accountId: string,
): Promise<ApiResponse<EntityRecord>> {
  return get<EntityRecord>(`/crm/accounts/${encodeURIComponent(accountId)}`);
}

/**
 * Create a new account record in the CRM service.
 *
 * POST /crm/accounts
 *
 * The CRM service will automatically generate the x_search composite field
 * from the submitted record fields (replicating the monolith's post-create
 * hook logic from WebVella.Erp.Plugins.Next/Hooks/Api/).
 *
 * @param account - EntityRecord containing account field values (name,
 *   industry, phone, email, website, etc.)
 * @returns The newly created account EntityRecord with server-assigned id
 */
export function createAccount(
  account: EntityRecord,
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>('/crm/accounts', account);
}

/**
 * Update an existing account record.
 *
 * PUT /crm/accounts/{accountId}
 *
 * The CRM service will automatically regenerate the x_search field on update
 * (replicating the monolith's post-update hook logic).
 *
 * @param accountId - UUID of the account to update
 * @param account - EntityRecord containing the fields to update
 * @returns The updated account EntityRecord
 */
export function updateAccount(
  accountId: string,
  account: EntityRecord,
): Promise<ApiResponse<EntityRecord>> {
  return put<EntityRecord>(
    `/crm/accounts/${encodeURIComponent(accountId)}`,
    account,
  );
}

/**
 * Delete an account record.
 *
 * DELETE /crm/accounts/{accountId}
 *
 * @param accountId - UUID of the account to delete
 * @returns Confirmation of successful deletion
 */
export function deleteAccount(
  accountId: string,
): Promise<ApiResponse<void>> {
  return del<void>(`/crm/accounts/${encodeURIComponent(accountId)}`);
}

// ---------------------------------------------------------------------------
// Contact endpoints — /crm/contacts
// ---------------------------------------------------------------------------

/**
 * List contact records with optional pagination, search, and filtering.
 *
 * GET /crm/contacts
 *
 * @param params - Optional query/pagination/sort/filter parameters
 * @returns Paginated array of contact EntityRecord objects
 */
export function listContacts(
  params?: CrmListParams,
): Promise<ApiResponse<EntityRecord[]>> {
  return get<EntityRecord[]>('/crm/contacts', buildListParams(params));
}

/**
 * Retrieve a single contact record by its unique identifier.
 *
 * GET /crm/contacts/{contactId}
 *
 * @param contactId - UUID of the contact to retrieve
 * @returns The matching contact EntityRecord
 */
export function getContact(
  contactId: string,
): Promise<ApiResponse<EntityRecord>> {
  return get<EntityRecord>(`/crm/contacts/${encodeURIComponent(contactId)}`);
}

/**
 * Create a new contact record in the CRM service.
 *
 * POST /crm/contacts
 *
 * The CRM service will automatically generate the x_search composite field
 * from the submitted record fields (first_name, last_name, email, phone,
 * etc.), replicating the monolith's post-create hook logic.
 *
 * @param contact - EntityRecord containing contact field values (first_name,
 *   last_name, email, phone, etc.)
 * @returns The newly created contact EntityRecord with server-assigned id
 */
export function createContact(
  contact: EntityRecord,
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>('/crm/contacts', contact);
}

/**
 * Update an existing contact record.
 *
 * PUT /crm/contacts/{contactId}
 *
 * The CRM service will automatically regenerate the x_search field on update.
 *
 * @param contactId - UUID of the contact to update
 * @param contact - EntityRecord containing the fields to update
 * @returns The updated contact EntityRecord
 */
export function updateContact(
  contactId: string,
  contact: EntityRecord,
): Promise<ApiResponse<EntityRecord>> {
  return put<EntityRecord>(
    `/crm/contacts/${encodeURIComponent(contactId)}`,
    contact,
  );
}

/**
 * Delete a contact record.
 *
 * DELETE /crm/contacts/{contactId}
 *
 * @param contactId - UUID of the contact to delete
 * @returns Confirmation of successful deletion
 */
export function deleteContact(
  contactId: string,
): Promise<ApiResponse<void>> {
  return del<void>(`/crm/contacts/${encodeURIComponent(contactId)}`);
}

// ---------------------------------------------------------------------------
// Cross-entity search — /crm/search
// ---------------------------------------------------------------------------

/**
 * Search CRM entities (accounts and/or contacts) using the x_search composite
 * field. This replaces the monolith's SearchService.RegenSearchField-powered
 * search where the x_search field aggregated indexed fields (including
 * relation fields via $relation_name.field_name syntax) for full-text lookup.
 *
 * GET /crm/search
 *
 * @param query - Free-text search string matched against x_search
 * @param entityType - Optional filter to restrict results to 'account' or
 *   'contact'. When omitted, both entity types are searched.
 * @returns Array of matching EntityRecord objects from one or both entity types
 */
export function searchCrm(
  query: string,
  entityType?: 'account' | 'contact',
): Promise<ApiResponse<EntityRecord[]>> {
  const params: Record<string, unknown> = { query };

  if (entityType !== undefined) {
    params['entityType'] = entityType;
  }

  return get<EntityRecord[]>('/crm/search', params);
}
