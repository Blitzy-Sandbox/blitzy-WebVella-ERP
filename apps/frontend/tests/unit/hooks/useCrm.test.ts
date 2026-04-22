/**
 * @file useCrm.test.ts
 * @description Comprehensive Vitest unit tests for the 15 CRM TanStack Query
 * hooks exported from src/hooks/useCrm.ts.
 *
 * These hooks replace:
 *   - NextPlugin.20190204.cs — Account, contact, address entity definitions
 *                              and relation wiring (account↔contact, contact↔address)
 *   - NextPlugin.20190206.cs — Salutation entity and contact↔salutation relation
 *   - SearchService.cs       — CRM x_search field indexing for accounts/contacts
 *   - AccountHook.cs         — Post-CRUD hooks for account search indexing
 *   - ContactHook.cs         — Post-CRUD hooks for contact search indexing
 *   - CrmPlugin.cs           — CRM plugin entry point
 *
 * Test suites cover all 15 hooks:
 *   - useAccounts         — paginated/filtered account listing
 *   - useAccount          — single account fetch by ID
 *   - useCreateAccount    — account creation mutation with cache invalidation
 *   - useUpdateAccount    — account update mutation with multi-key invalidation
 *   - useDeleteAccount    — account deletion mutation
 *   - useContacts         — paginated/filtered contact listing
 *   - useContact          — single contact fetch by ID
 *   - useCreateContact    — contact creation mutation with cache invalidation
 *   - useUpdateContact    — contact update mutation with multi-key invalidation
 *   - useDeleteContact    — contact deletion mutation
 *   - useAddresses        — parent-linked address listing
 *   - useCreateAddress    — address creation mutation with cache invalidation
 *   - useUpdateAddress    — address update mutation with cache invalidation
 *   - useCrmSearch        — cross-entity CRM search
 *   - useSalutations      — salutation reference data with 30-min staleTime
 *
 * Mocking strategy:
 *   - vi.mock intercepts the API client module to prevent real HTTP calls
 *   - A fresh QueryClient (retry: false) is created for each test
 *   - invalidateQueries spies verify cache key invalidation patterns
 *
 * @module tests/unit/hooks/useCrm.test
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';

// ──────────────────────────────────────────────────────────────────────────────
// Module mocks — vi.mock calls are hoisted by Vitest before all imports
// ──────────────────────────────────────────────────────────────────────────────

vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  del: vi.fn(),
  default: {},
}));

// ──────────────────────────────────────────────────────────────────────────────
// Module-under-test import (uses mocked dependencies)
// ──────────────────────────────────────────────────────────────────────────────

import {
  useAccounts,
  useAccount,
  useCreateAccount,
  useUpdateAccount,
  useDeleteAccount,
  useContacts,
  useContact,
  useCreateContact,
  useUpdateContact,
  useDeleteContact,
  useAddresses,
  useCreateAddress,
  useUpdateAddress,
  useCrmSearch,
  useSalutations,
} from '../../../src/hooks/useCrm';

// ──────────────────────────────────────────────────────────────────────────────
// Mocked module imports (for typed access to mocks)
// ──────────────────────────────────────────────────────────────────────────────

import { get, post, put, del } from '../../../src/api/client';

// ──────────────────────────────────────────────────────────────────────────────
// Type imports
// ──────────────────────────────────────────────────────────────────────────────

import type {
  EntityRecord,
  EntityRecordList,
  RecordResponse,
  RecordListResponse,
} from '../../../src/types/record';
import type { BaseResponseModel, SearchResultList } from '../../../src/types/common';

// ──────────────────────────────────────────────────────────────────────────────
// Typed mock references
// ──────────────────────────────────────────────────────────────────────────────

const mockGet = vi.mocked(get);
const mockPost = vi.mocked(post);
const mockPut = vi.mocked(put);
const mockDel = vi.mocked(del);

// ──────────────────────────────────────────────────────────────────────────────
// Test fixtures — derived from NextPlugin.20190204.cs and NextPlugin.20190206.cs
// entity definitions
// ──────────────────────────────────────────────────────────────────────────────

/** Stable ISO timestamp used across all mock responses for deterministic tests */
const MOCK_TIMESTAMP = '2024-01-15T12:00:00.000Z';

/**
 * Mock account record matching the account entity fields defined in
 * NextPlugin.20190204.cs (name, phone, email, website UrlField, city,
 * state, country, type SelectField, industry).
 */
const mockAccount: EntityRecord = {
  id: 'account-guid',
  name: 'Acme Corp',
  phone: '+1-555-0100',
  email: 'info@acme.com',
  website: 'https://acme.com',
  city: 'New York',
  state: 'NY',
  country: 'US',
  type: 'customer',
  industry: 'technology',
};

/**
 * Second mock account used in list responses and multi-result assertions.
 */
const mockAccount2: EntityRecord = {
  id: 'account-guid-2',
  name: 'Globex Inc',
  phone: '+1-555-0200',
  email: 'info@globex.com',
  website: 'https://globex.com',
  city: 'San Francisco',
  state: 'CA',
  country: 'US',
  type: 'prospect',
  industry: 'manufacturing',
};

/**
 * Mock contact record matching the contact entity fields defined in
 * NextPlugin.20190204.cs (first_name, last_name, email, phone, company,
 * job_title) and NextPlugin.20190206.cs (salutation_id relation).
 */
const mockContact: EntityRecord = {
  id: 'contact-guid',
  first_name: 'John',
  last_name: 'Doe',
  email: 'john@acme.com',
  phone: '+1-555-0101',
  company: 'Acme Corp',
  job_title: 'CTO',
  salutation_id: 'mr-guid',
};

/**
 * Second mock contact used in list responses.
 */
const mockContact2: EntityRecord = {
  id: 'contact-guid-2',
  first_name: 'Jane',
  last_name: 'Smith',
  email: 'jane@acme.com',
  phone: '+1-555-0102',
  company: 'Acme Corp',
  job_title: 'CFO',
  salutation_id: 'mrs-guid',
};

/**
 * Mock address record matching the address entity fields defined in
 * NextPlugin.20190204.cs (street, city, state, zip, country).
 */
const mockAddress: EntityRecord = {
  id: 'address-guid',
  street: '123 Main St',
  city: 'New York',
  state: 'NY',
  zip: '10001',
  country: 'US',
};

/**
 * Mock salutation reference data record from the salutation entity
 * created by NextPlugin.20190206.cs.
 */
const mockSalutation: EntityRecord = {
  id: 'mr-guid',
  name: 'Mr',
  label: 'Mr.',
};

/**
 * Second mock salutation for list assertion.
 */
const mockSalutation2: EntityRecord = {
  id: 'mrs-guid',
  name: 'Mrs',
  label: 'Mrs.',
};

// ──────────────────────────────────────────────────────────────────────────────
// Response envelope helpers
// ──────────────────────────────────────────────────────────────────────────────

/**
 * Creates a mock ApiResponse<T> envelope matching the shape from
 * api/client.ts (ApiResponse interface: success, errors, statusCode,
 * timestamp, message, object).
 *
 * @param object - Typed payload for the response
 * @param success - Whether the operation succeeded
 * @param message - Human-readable message
 * @returns Complete ApiResponse envelope
 */
function apiResponse<T>(object: T, success = true, message = 'Success') {
  return {
    success,
    errors: [] as Array<{ key: string; value: string; message: string }>,
    statusCode: success ? 200 : 400,
    timestamp: MOCK_TIMESTAMP,
    message,
    object,
  };
}

/**
 * Creates a mock ApiResponse for list endpoints returning EntityRecordList.
 * Wraps records in the { records, totalCount } shape expected by
 * RecordListResponse['object'].
 */
function listResponse(records: EntityRecord[], totalCount?: number) {
  return apiResponse<EntityRecordList>({
    records,
    totalCount: totalCount ?? records.length,
  });
}

/**
 * Creates a mock ApiResponse for single record endpoints returning EntityRecord.
 */
function recordResponse(record: EntityRecord) {
  return apiResponse<EntityRecord>(record);
}

/**
 * Creates a void success response envelope for delete operations.
 * The hook only checks success + errors, no typed object expected.
 */
function deleteSuccessResponse(message = 'Deleted') {
  return {
    success: true,
    errors: [] as Array<{ key: string; value: string; message: string }>,
    statusCode: 200,
    timestamp: MOCK_TIMESTAMP,
    message,
  };
}

/**
 * Creates a failed API response with success: false for testing
 * assertApiSuccess() error path in hooks. When hooks receive this,
 * assertApiSuccess throws an Error with concatenated error messages.
 */
function failedApiResponse(
  message: string,
  errors: Array<{ key: string; value: string; message: string }> = [],
) {
  return {
    success: false,
    errors,
    statusCode: 400,
    timestamp: MOCK_TIMESTAMP,
    message,
    object: undefined,
  };
}

// ──────────────────────────────────────────────────────────────────────────────
// QueryClient and wrapper setup
// ──────────────────────────────────────────────────────────────────────────────

let queryClient: QueryClient;

/**
 * Creates a React component wrapper that provides QueryClientProvider context
 * for renderHook calls. Uses createElement instead of JSX to avoid needing
 * a .tsx file extension for this pure-logic test file.
 */
function createWrapper() {
  return ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children);
}

let wrapper: ReturnType<typeof createWrapper>;

beforeEach(() => {
  queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  wrapper = createWrapper();
});

afterEach(() => {
  vi.clearAllMocks();
  queryClient.clear();
});

// ═══════════════════════════════════════════════════════════════════════════════
// ACCOUNT QUERY HOOKS
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useAccounts — Paginated/filtered account listing
// Replaces: RecordManager.Find("account", query) + SearchService x_search
// Endpoint: GET /v1/crm/accounts
// ---------------------------------------------------------------------------

describe('useAccounts', () => {
  it('should fetch accounts', async () => {
    mockGet.mockResolvedValueOnce(listResponse([mockAccount, mockAccount2]) as never);

    const { result } = renderHook(() => useAccounts(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/accounts', undefined);
    expect(result.current.data).toEqual({
      records: [mockAccount, mockAccount2],
      totalCount: 2,
    });
  });

  it('should filter by industry', async () => {
    mockGet.mockResolvedValueOnce(listResponse([mockAccount]) as never);

    const { result } = renderHook(
      () => useAccounts({ industry: 'technology' }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/accounts', {
      industry: 'technology',
    });
  });

  it('should filter by type', async () => {
    mockGet.mockResolvedValueOnce(listResponse([mockAccount]) as never);

    const { result } = renderHook(
      () => useAccounts({ type: 'customer' }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/accounts', {
      type: 'customer',
    });
  });

  it('should filter by city', async () => {
    mockGet.mockResolvedValueOnce(listResponse([mockAccount]) as never);

    const { result } = renderHook(
      () => useAccounts({ city: 'New York' }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/accounts', {
      city: 'New York',
    });
  });

  it('should support search', async () => {
    mockGet.mockResolvedValueOnce(listResponse([mockAccount]) as never);

    const { result } = renderHook(
      () => useAccounts({ search: 'acme' }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/accounts', {
      search: 'acme',
    });
  });

  it('should support pagination and sorting', async () => {
    mockGet.mockResolvedValueOnce(
      listResponse([mockAccount2], 15) as never,
    );

    const { result } = renderHook(
      () => useAccounts({ page: 2, pageSize: 10, sort: 'name:asc' }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/accounts', {
      page: 2,
      pageSize: 10,
      sort: 'name:asc',
    });
    expect(result.current.data?.totalCount).toBe(15);
  });

  it('should handle combined filters', async () => {
    mockGet.mockResolvedValueOnce(listResponse([mockAccount]) as never);

    const { result } = renderHook(
      () =>
        useAccounts({
          search: 'acme',
          industry: 'technology',
          type: 'customer',
          city: 'New York',
          page: 1,
          pageSize: 25,
          sort: 'name:desc',
        }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/accounts', {
      search: 'acme',
      industry: 'technology',
      type: 'customer',
      city: 'New York',
      page: 1,
      pageSize: 25,
      sort: 'name:desc',
    });
  });

  it('should handle API error', async () => {
    mockGet.mockResolvedValueOnce(
      failedApiResponse('Failed to fetch accounts', [
        { key: 'server', value: '', message: 'Database unavailable' },
      ]) as never,
    );

    const { result } = renderHook(() => useAccounts(), { wrapper });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Database unavailable');
  });
});

// ---------------------------------------------------------------------------
// useAccount — Single account fetch by ID
// Replaces: RecordManager.Find("account") with ID filter
// Endpoint: GET /v1/crm/accounts/{id}
// ---------------------------------------------------------------------------

describe('useAccount', () => {
  it('should fetch account by ID', async () => {
    mockGet.mockResolvedValueOnce(recordResponse(mockAccount) as never);

    const { result } = renderHook(
      () => useAccount('account-guid'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/accounts/account-guid');
    expect(result.current.data).toEqual(mockAccount);
  });

  it('should include related contacts', async () => {
    const accountWithContacts: EntityRecord = {
      ...mockAccount,
      contacts: [mockContact, mockContact2],
    };
    mockGet.mockResolvedValueOnce(
      recordResponse(accountWithContacts) as never,
    );

    const { result } = renderHook(
      () => useAccount('account-guid'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const account = result.current.data as EntityRecord;
    expect(account.contacts).toBeDefined();
    expect(account.contacts).toHaveLength(2);
  });

  it('should not fetch when id is empty string', () => {
    const { result } = renderHook(() => useAccount(''), { wrapper });

    // Query should be disabled — fetchStatus 'idle' indicates no fetch initiated
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });

  it('should handle non-existent account', async () => {
    mockGet.mockResolvedValueOnce(
      failedApiResponse('Account not found', [
        { key: 'id', value: 'nonexistent', message: 'Account not found' },
      ]) as never,
    );

    const { result } = renderHook(
      () => useAccount('nonexistent'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Account not found');
  });
});

// ═══════════════════════════════════════════════════════════════════════════════
// ACCOUNT MUTATION HOOKS
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useCreateAccount — Account creation mutation
// Replaces: RecordManager.CreateRecord("account", record) → AccountHook.OnPostCreateRecord
// Endpoint: POST /v1/crm/accounts
// Cache invalidation: ['accounts']
// ---------------------------------------------------------------------------

describe('useCreateAccount', () => {
  it('should create account', async () => {
    mockPost.mockResolvedValueOnce(recordResponse(mockAccount) as never);

    const { result } = renderHook(() => useCreateAccount(), { wrapper });

    await act(async () => {
      result.current.mutate({
        name: 'Acme Corp',
        email: 'info@acme.com',
        phone: '+1-555-0100',
        industry: 'technology',
        type: 'customer',
      } as EntityRecord);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockPost).toHaveBeenCalledWith('/crm/accounts', {
      name: 'Acme Corp',
      email: 'info@acme.com',
      phone: '+1-555-0100',
      industry: 'technology',
      type: 'customer',
    });
    expect(result.current.data).toEqual(mockAccount);
  });

  it('should invalidate accounts query on success', async () => {
    mockPost.mockResolvedValueOnce(recordResponse(mockAccount) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateAccount(), { wrapper });

    await act(async () => {
      result.current.mutate({
        name: 'New Account',
        email: 'new@test.com',
      } as EntityRecord);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['accounts'],
    });
  });

  it('should handle validation error', async () => {
    mockPost.mockResolvedValueOnce(
      failedApiResponse('Validation failed', [
        { key: 'name', value: '', message: 'Account name is required' },
      ]) as never,
    );

    const { result } = renderHook(() => useCreateAccount(), { wrapper });

    await act(async () => {
      result.current.mutate({} as EntityRecord);
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Account name is required');
  });
});

// ---------------------------------------------------------------------------
// useUpdateAccount — Account update mutation
// Replaces: RecordManager.UpdateRecord("account", record) → AccountHook.OnPostUpdateRecord
// Endpoint: PUT /v1/crm/accounts/{id}
// Cache invalidation: ['accounts'] + ['accounts', id]
// ---------------------------------------------------------------------------

describe('useUpdateAccount', () => {
  it('should update account', async () => {
    const updatedAccount = { ...mockAccount, name: 'Acme Corporation' };
    mockPut.mockResolvedValueOnce(recordResponse(updatedAccount) as never);

    const { result } = renderHook(() => useUpdateAccount(), { wrapper });

    await act(async () => {
      result.current.mutate({
        id: 'account-guid',
        data: { name: 'Acme Corporation' } as EntityRecord,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockPut).toHaveBeenCalledWith(
      '/crm/accounts/account-guid',
      { name: 'Acme Corporation' },
    );
    expect(result.current.data).toEqual(updatedAccount);
  });

  it('should invalidate accounts list and specific account', async () => {
    const updatedAccount = { ...mockAccount, name: 'Updated' };
    mockPut.mockResolvedValueOnce(recordResponse(updatedAccount) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateAccount(), { wrapper });

    await act(async () => {
      result.current.mutate({
        id: 'account-guid',
        data: { name: 'Updated' } as EntityRecord,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Should invalidate the broad accounts list cache
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['accounts'],
    });
    // Should also invalidate the specific account detail cache
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['accounts', 'account-guid'],
    });
  });
});

// ---------------------------------------------------------------------------
// useDeleteAccount — Account deletion mutation
// Replaces: RecordManager.DeleteRecord("account", recordId)
// Endpoint: DELETE /v1/crm/accounts/{id}
// Cache invalidation: ['accounts']
// ---------------------------------------------------------------------------

describe('useDeleteAccount', () => {
  it('should delete account', async () => {
    mockDel.mockResolvedValueOnce(deleteSuccessResponse() as never);

    const { result } = renderHook(() => useDeleteAccount(), { wrapper });

    await act(async () => {
      result.current.mutate('account-guid');
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockDel).toHaveBeenCalledWith('/crm/accounts/account-guid');
  });

  it('should invalidate accounts query on success', async () => {
    mockDel.mockResolvedValueOnce(deleteSuccessResponse() as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteAccount(), { wrapper });

    await act(async () => {
      result.current.mutate('account-guid');
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['accounts'],
    });
  });

  it('should handle delete failure', async () => {
    mockDel.mockResolvedValueOnce(
      failedApiResponse('Cannot delete account with active contacts') as never,
    );

    const { result } = renderHook(() => useDeleteAccount(), { wrapper });

    await act(async () => {
      result.current.mutate('account-guid');
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe(
      'Cannot delete account with active contacts',
    );
  });
});

// ═══════════════════════════════════════════════════════════════════════════════
// CONTACT QUERY HOOKS
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useContacts — Paginated/filtered contact listing
// Replaces: RecordManager.Find("contact", query) + SearchService x_search
// Endpoint: GET /v1/crm/contacts
// ---------------------------------------------------------------------------

describe('useContacts', () => {
  it('should fetch contacts', async () => {
    mockGet.mockResolvedValueOnce(
      listResponse([mockContact, mockContact2]) as never,
    );

    const { result } = renderHook(() => useContacts(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/contacts', undefined);
    expect(result.current.data).toEqual({
      records: [mockContact, mockContact2],
      totalCount: 2,
    });
  });

  it('should filter by accountId', async () => {
    mockGet.mockResolvedValueOnce(listResponse([mockContact]) as never);

    const { result } = renderHook(
      () => useContacts({ accountId: 'account-guid' }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/contacts', {
      accountId: 'account-guid',
    });
  });

  it('should filter by salutationId', async () => {
    mockGet.mockResolvedValueOnce(listResponse([mockContact]) as never);

    const { result } = renderHook(
      () => useContacts({ salutationId: 'mr-guid' }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/contacts', {
      salutationId: 'mr-guid',
    });
  });

  it('should support search and pagination', async () => {
    mockGet.mockResolvedValueOnce(
      listResponse([mockContact], 50) as never,
    );

    const { result } = renderHook(
      () =>
        useContacts({
          search: 'john',
          page: 1,
          pageSize: 25,
          sort: 'last_name:asc',
        }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/contacts', {
      search: 'john',
      page: 1,
      pageSize: 25,
      sort: 'last_name:asc',
    });
    expect(result.current.data?.totalCount).toBe(50);
  });

  it('should handle combined contact filters', async () => {
    mockGet.mockResolvedValueOnce(listResponse([mockContact]) as never);

    const { result } = renderHook(
      () =>
        useContacts({
          search: 'doe',
          accountId: 'account-guid',
          salutationId: 'mr-guid',
          page: 2,
          pageSize: 10,
          sort: 'email:asc',
        }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/contacts', {
      search: 'doe',
      accountId: 'account-guid',
      salutationId: 'mr-guid',
      page: 2,
      pageSize: 10,
      sort: 'email:asc',
    });
  });
});

// ---------------------------------------------------------------------------
// useContact — Single contact fetch by ID
// Replaces: RecordManager.Find("contact") with ID filter
// Endpoint: GET /v1/crm/contacts/{id}
// ---------------------------------------------------------------------------

describe('useContact', () => {
  it('should fetch contact by ID', async () => {
    mockGet.mockResolvedValueOnce(recordResponse(mockContact) as never);

    const { result } = renderHook(
      () => useContact('contact-guid'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/contacts/contact-guid');
    expect(result.current.data).toEqual(mockContact);
  });

  it('should include salutation data', async () => {
    const contactWithSalutation: EntityRecord = {
      ...mockContact,
      salutation: mockSalutation,
    };
    mockGet.mockResolvedValueOnce(
      recordResponse(contactWithSalutation) as never,
    );

    const { result } = renderHook(
      () => useContact('contact-guid'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const contact = result.current.data as EntityRecord;
    expect(contact.salutation).toBeDefined();
    expect((contact.salutation as EntityRecord).name).toBe('Mr');
  });

  it('should not fetch when id is empty string', () => {
    const { result } = renderHook(() => useContact(''), { wrapper });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });

  it('should handle non-existent contact', async () => {
    mockGet.mockResolvedValueOnce(
      failedApiResponse('Contact not found', [
        { key: 'id', value: 'nonexistent', message: 'Contact not found' },
      ]) as never,
    );

    const { result } = renderHook(
      () => useContact('nonexistent'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Contact not found');
  });
});

// ═══════════════════════════════════════════════════════════════════════════════
// CONTACT MUTATION HOOKS
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useCreateContact — Contact creation mutation
// Replaces: RecordManager.CreateRecord("contact", record) → ContactHook.OnPostCreateRecord
// Endpoint: POST /v1/crm/contacts
// Cache invalidation: ['contacts']
// ---------------------------------------------------------------------------

describe('useCreateContact', () => {
  it('should create contact', async () => {
    mockPost.mockResolvedValueOnce(recordResponse(mockContact) as never);

    const { result } = renderHook(() => useCreateContact(), { wrapper });

    await act(async () => {
      result.current.mutate({
        first_name: 'John',
        last_name: 'Doe',
        email: 'john@acme.com',
        phone: '+1-555-0101',
        company: 'Acme Corp',
        job_title: 'CTO',
        salutation_id: 'mr-guid',
      } as EntityRecord);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockPost).toHaveBeenCalledWith('/crm/contacts', {
      first_name: 'John',
      last_name: 'Doe',
      email: 'john@acme.com',
      phone: '+1-555-0101',
      company: 'Acme Corp',
      job_title: 'CTO',
      salutation_id: 'mr-guid',
    });
    expect(result.current.data).toEqual(mockContact);
  });

  it('should invalidate contacts query on success', async () => {
    mockPost.mockResolvedValueOnce(recordResponse(mockContact) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateContact(), { wrapper });

    await act(async () => {
      result.current.mutate({
        first_name: 'Jane',
        last_name: 'Smith',
        email: 'jane@acme.com',
      } as EntityRecord);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['contacts'],
    });
  });
});

// ---------------------------------------------------------------------------
// useUpdateContact — Contact update mutation
// Replaces: RecordManager.UpdateRecord("contact", record) → ContactHook.OnPostUpdateRecord
// Endpoint: PUT /v1/crm/contacts/{id}
// Cache invalidation: ['contacts'] + ['contacts', id]
// ---------------------------------------------------------------------------

describe('useUpdateContact', () => {
  it('should update contact', async () => {
    const updatedContact = { ...mockContact, job_title: 'CEO' };
    mockPut.mockResolvedValueOnce(recordResponse(updatedContact) as never);

    const { result } = renderHook(() => useUpdateContact(), { wrapper });

    await act(async () => {
      result.current.mutate({
        id: 'contact-guid',
        data: { job_title: 'CEO' } as EntityRecord,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockPut).toHaveBeenCalledWith(
      '/crm/contacts/contact-guid',
      { job_title: 'CEO' },
    );
    expect(result.current.data).toEqual(updatedContact);
  });

  it('should invalidate contacts list and specific contact', async () => {
    const updatedContact = { ...mockContact, email: 'updated@acme.com' };
    mockPut.mockResolvedValueOnce(recordResponse(updatedContact) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateContact(), { wrapper });

    await act(async () => {
      result.current.mutate({
        id: 'contact-guid',
        data: { email: 'updated@acme.com' } as EntityRecord,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Should invalidate the broad contacts list cache
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['contacts'],
    });
    // Should also invalidate the specific contact detail cache
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['contacts', 'contact-guid'],
    });
  });
});

// ---------------------------------------------------------------------------
// useDeleteContact — Contact deletion mutation
// Replaces: RecordManager.DeleteRecord("contact", recordId)
// Endpoint: DELETE /v1/crm/contacts/{id}
// Cache invalidation: ['contacts']
// ---------------------------------------------------------------------------

describe('useDeleteContact', () => {
  it('should delete contact', async () => {
    mockDel.mockResolvedValueOnce(deleteSuccessResponse() as never);

    const { result } = renderHook(() => useDeleteContact(), { wrapper });

    await act(async () => {
      result.current.mutate('contact-guid');
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockDel).toHaveBeenCalledWith('/crm/contacts/contact-guid');
  });

  it('should invalidate contacts query on success', async () => {
    mockDel.mockResolvedValueOnce(deleteSuccessResponse() as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteContact(), { wrapper });

    await act(async () => {
      result.current.mutate('contact-guid');
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['contacts'],
    });
  });

  it('should handle delete failure', async () => {
    mockDel.mockResolvedValueOnce(
      failedApiResponse('Cannot delete contact') as never,
    );

    const { result } = renderHook(() => useDeleteContact(), { wrapper });

    await act(async () => {
      result.current.mutate('contact-guid');
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Cannot delete contact');
  });
});

// ═══════════════════════════════════════════════════════════════════════════════
// ADDRESS HOOKS
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useAddresses — Parent-linked address listing
// Replaces: EQL $relation navigation for account↔address, contact↔address
// Endpoint: GET /v1/crm/addresses?parentEntityId={}&parentRecordId={}
// ---------------------------------------------------------------------------

describe('useAddresses', () => {
  it('should fetch addresses for account', async () => {
    mockGet.mockResolvedValueOnce(listResponse([mockAddress]) as never);

    const { result } = renderHook(
      () => useAddresses('account', 'account-guid'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/addresses', {
      parentEntityId: 'account',
      parentRecordId: 'account-guid',
    });
    expect(result.current.data).toEqual({
      records: [mockAddress],
      totalCount: 1,
    });
  });

  it('should fetch addresses for contact', async () => {
    const contactAddress: EntityRecord = {
      id: 'address-guid-2',
      street: '456 Oak Ave',
      city: 'Boston',
      state: 'MA',
      zip: '02101',
      country: 'US',
    };
    mockGet.mockResolvedValueOnce(listResponse([contactAddress]) as never);

    const { result } = renderHook(
      () => useAddresses('contact', 'contact-guid'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/addresses', {
      parentEntityId: 'contact',
      parentRecordId: 'contact-guid',
    });
    expect(result.current.data?.records).toHaveLength(1);
    expect(result.current.data?.records[0].city).toBe('Boston');
  });

  it('should not fetch when parentEntityId is empty', () => {
    const { result } = renderHook(
      () => useAddresses('', 'some-record-id'),
      { wrapper },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });

  it('should not fetch when parentRecordId is empty', () => {
    const { result } = renderHook(
      () => useAddresses('account', ''),
      { wrapper },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// useCreateAddress — Address creation mutation
// Replaces: RecordManager.CreateRecord("address", record)
// Endpoint: POST /v1/crm/addresses
// Cache invalidation: ['addresses']
// ---------------------------------------------------------------------------

describe('useCreateAddress', () => {
  it('should create address', async () => {
    mockPost.mockResolvedValueOnce(recordResponse(mockAddress) as never);

    const { result } = renderHook(() => useCreateAddress(), { wrapper });

    await act(async () => {
      result.current.mutate({
        street: '123 Main St',
        city: 'New York',
        state: 'NY',
        zip: '10001',
        country: 'US',
        parentEntityId: 'account',
        parentRecordId: 'account-guid',
      } as EntityRecord);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockPost).toHaveBeenCalledWith('/crm/addresses', {
      street: '123 Main St',
      city: 'New York',
      state: 'NY',
      zip: '10001',
      country: 'US',
      parentEntityId: 'account',
      parentRecordId: 'account-guid',
    });
    expect(result.current.data).toEqual(mockAddress);
  });

  it('should invalidate addresses query on success', async () => {
    mockPost.mockResolvedValueOnce(recordResponse(mockAddress) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateAddress(), { wrapper });

    await act(async () => {
      result.current.mutate({
        street: '789 Elm Rd',
        city: 'Chicago',
        state: 'IL',
        zip: '60601',
        country: 'US',
      } as EntityRecord);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['addresses'],
    });
  });
});

// ---------------------------------------------------------------------------
// useUpdateAddress — Address update mutation
// Replaces: RecordManager.UpdateRecord("address", record)
// Endpoint: PUT /v1/crm/addresses/{id}
// Cache invalidation: ['addresses']
// ---------------------------------------------------------------------------

describe('useUpdateAddress', () => {
  it('should update address', async () => {
    const updatedAddress = { ...mockAddress, street: '456 Broadway' };
    mockPut.mockResolvedValueOnce(recordResponse(updatedAddress) as never);

    const { result } = renderHook(() => useUpdateAddress(), { wrapper });

    await act(async () => {
      result.current.mutate({
        id: 'address-guid',
        data: { street: '456 Broadway' } as EntityRecord,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockPut).toHaveBeenCalledWith(
      '/crm/addresses/address-guid',
      { street: '456 Broadway' },
    );
    expect(result.current.data).toEqual(updatedAddress);
  });

  it('should invalidate addresses query on success', async () => {
    const updatedAddress = { ...mockAddress, zip: '10002' };
    mockPut.mockResolvedValueOnce(recordResponse(updatedAddress) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateAddress(), { wrapper });

    await act(async () => {
      result.current.mutate({
        id: 'address-guid',
        data: { zip: '10002' } as EntityRecord,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['addresses'],
    });
  });
});

// ═══════════════════════════════════════════════════════════════════════════════
// CRM SEARCH HOOK
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useCrmSearch — Cross-entity CRM search
// Replaces: SearchService.RegenSearchField + x_search querying pattern
// Endpoint: GET /v1/crm/search?q={query}
// ---------------------------------------------------------------------------

describe('useCrmSearch', () => {
  it('should search across CRM entities', async () => {
    const searchResults: SearchResultList = {
      results: [
        {
          id: 'search-1',
          entities: ['account-entity-guid'],
          apps: [],
          records: ['account-guid'],
          content: 'Acme Corp — technology company in New York',
          stemContent: 'acme corp technolog compani new york',
          snippet: '<em>Acme</em> Corp — technology company',
          url: '/crm/accounts/account-guid',
          auxData: '',
          timestamp: MOCK_TIMESTAMP,
        },
      ],
      totalCount: 1,
    };
    mockGet.mockResolvedValueOnce(
      apiResponse<SearchResultList>(searchResults) as never,
    );

    const { result } = renderHook(
      () => useCrmSearch('acme'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/search', { q: 'acme' });
    expect(result.current.data?.totalCount).toBe(1);
  });

  it('should return results from accounts and contacts', async () => {
    const searchResults: SearchResultList = {
      results: [
        {
          id: 'search-1',
          entities: ['account-entity-guid'],
          apps: [],
          records: ['account-guid'],
          content: 'Acme Corp — technology company',
          stemContent: 'acme corp technolog',
          snippet: '<em>Acme</em> Corp',
          url: '/crm/accounts/account-guid',
          auxData: '',
          timestamp: MOCK_TIMESTAMP,
        },
        {
          id: 'search-2',
          entities: ['contact-entity-guid'],
          apps: [],
          records: ['contact-guid'],
          content: 'John Doe — CTO at Acme Corp',
          stemContent: 'john doe cto acme',
          snippet: 'John Doe — CTO at <em>Acme</em>',
          url: '/crm/contacts/contact-guid',
          auxData: '',
          timestamp: MOCK_TIMESTAMP,
        },
        {
          id: 'search-3',
          entities: ['contact-entity-guid'],
          apps: [],
          records: ['contact-guid-2'],
          content: 'Jane Smith — CFO at Acme Corp',
          stemContent: 'jane smith cfo acme',
          snippet: 'Jane Smith — CFO at <em>Acme</em>',
          url: '/crm/contacts/contact-guid-2',
          auxData: '',
          timestamp: MOCK_TIMESTAMP,
        },
      ],
      totalCount: 3,
    };
    mockGet.mockResolvedValueOnce(
      apiResponse<SearchResultList>(searchResults) as never,
    );

    const { result } = renderHook(
      () => useCrmSearch('acme'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const data = result.current.data as SearchResultList;
    expect(data.results).toHaveLength(3);
    expect(data.totalCount).toBe(3);

    // Verify mixed entity types in results — account and contact entities
    const entityIds = data.results.map((r) => r.entities[0]);
    expect(entityIds).toContain('account-entity-guid');
    expect(entityIds).toContain('contact-entity-guid');
  });

  it('should not execute with empty query', () => {
    const { result } = renderHook(
      () => useCrmSearch(''),
      { wrapper },
    );

    // Hook is disabled when query is empty — no API call made
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });

  it('should not execute with whitespace-only query', () => {
    const { result } = renderHook(
      () => useCrmSearch('   '),
      { wrapper },
    );

    // Hook checks query.trim().length > 0, whitespace-only is disabled
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });

  it('should handle search failure', async () => {
    mockGet.mockResolvedValueOnce(
      failedApiResponse('CRM search failed', [
        { key: 'query', value: 'bad', message: 'Search index unavailable' },
      ]) as never,
    );

    const { result } = renderHook(
      () => useCrmSearch('bad'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Search index unavailable');
  });
});

// ═══════════════════════════════════════════════════════════════════════════════
// SALUTATION REFERENCE DATA HOOK
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useSalutations — Salutation reference data
// Replaces: Querying the 'salutation' entity from NextPlugin.20190206.cs
// Endpoint: GET /v1/crm/salutations
// staleTime: 30 minutes (1 800 000 ms) — reference data rarely changes
// ---------------------------------------------------------------------------

describe('useSalutations', () => {
  it('should fetch salutations', async () => {
    mockGet.mockResolvedValueOnce(
      listResponse([mockSalutation, mockSalutation2]) as never,
    );

    const { result } = renderHook(() => useSalutations(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/crm/salutations');
    expect(result.current.data).toEqual({
      records: [mockSalutation, mockSalutation2],
      totalCount: 2,
    });
  });

  it('should use staleTime of 30 minutes', async () => {
    mockGet.mockResolvedValue(
      listResponse([mockSalutation, mockSalutation2]) as never,
    );

    // First render — triggers initial fetch
    const { result } = renderHook(() => useSalutations(), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockGet).toHaveBeenCalledTimes(1);

    mockGet.mockClear();

    // Second render with same QueryClient — data should be served from cache
    // because it is within the 30-minute staleTime window (SALUTATION_STALE_TIME_MS)
    const { result: result2 } = renderHook(
      () => useSalutations(),
      { wrapper },
    );
    await waitFor(() => expect(result2.current.isSuccess).toBe(true));

    // No additional API call — data is still fresh within the 30-minute window
    expect(mockGet).not.toHaveBeenCalled();
  });

  it('should return salutation records with name and label', async () => {
    const salutations: EntityRecord[] = [
      { id: 'mr-guid', name: 'Mr', label: 'Mr.' },
      { id: 'mrs-guid', name: 'Mrs', label: 'Mrs.' },
      { id: 'ms-guid', name: 'Ms', label: 'Ms.' },
      { id: 'dr-guid', name: 'Dr', label: 'Dr.' },
    ];
    mockGet.mockResolvedValueOnce(listResponse(salutations) as never);

    const { result } = renderHook(() => useSalutations(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const data = result.current.data;
    expect(data?.records).toHaveLength(4);
    expect(data?.totalCount).toBe(4);

    // Verify each salutation has required fields for the select dropdown
    for (const sal of data?.records ?? []) {
      expect(sal.id).toBeDefined();
      expect(sal.name).toBeDefined();
      expect(sal.label).toBeDefined();
    }
  });

  it('should handle fetch failure', async () => {
    mockGet.mockResolvedValueOnce(
      failedApiResponse('Failed to fetch salutations') as never,
    );

    const { result } = renderHook(() => useSalutations(), { wrapper });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Failed to fetch salutations');
  });
});
