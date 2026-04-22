/**
 * @file useSearch.test.ts
 * @description Comprehensive Vitest unit tests for the 6 global search TanStack
 * Query hooks exported from src/hooks/useSearch.ts.
 *
 * Hooks under test:
 *   useGlobalSearch        — POST /v1/search (replaces SearchManager.Search)
 *   useSearchSuggestions   — GET /v1/search/suggestions (replaces AppSearchService stub)
 *   useSearchResults       — GET /v1/search/results/{searchId} (paginated result retrieval)
 *   useAddToSearchIndex    — POST /v1/search/index (replaces SearchManager.AddToIndex)
 *   useRemoveFromSearchIndex — DELETE /v1/search/index/{id} (replaces SearchManager.RemoveFromIndex)
 *   useRebuildSearchIndex  — POST /v1/search/rebuild (admin full rebuild)
 *
 * Replaces monolith subsystems:
 *   - SearchManager.cs — PostgreSQL FTS-based system_search with Contains and Fts modes,
 *     entity/app/record filters, LIMIT/OFFSET pagination, COUNT(*) OVER() windowed totals,
 *     and FtsAnalyzer stemming for stem_content column indexing
 *   - AppSearchService.cs — stub marked "//TO BE DEVELOPED" for typeahead suggestions
 *
 * Key behaviours validated:
 *   - Contains mode: case-insensitive ILIKE per word against content column
 *   - FTS mode: to_tsvector/to_tsquery with stemming via FtsAnalyzer.ProcessText
 *   - Entity/app/record filter combinations (OR-chained ILIKE in source)
 *   - Windowed total count (COUNT(*) OVER() pattern → totalCount field)
 *   - Pagination via skip/limit (OFFSET/LIMIT in source)
 *   - staleTime: 30s for search results, 60s for suggestions
 *   - enabled: disabled when text is empty (global search) or < 2 chars (suggestions)
 *   - Cache invalidation: ['search'] on add/remove mutations
 *   - No cache invalidation on rebuild (long-running background task)
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React, { type ReactNode } from 'react';

// ──────────────────────────────────────────────────────────────────────────────
// Module mock — vi.mock is hoisted by Vitest before all imports
// ──────────────────────────────────────────────────────────────────────────────

vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  del: vi.fn(),
  patch: vi.fn(),
  default: {},
}));

// ──────────────────────────────────────────────────────────────────────────────
// Module-under-test imports (uses mocked client dependency)
// ──────────────────────────────────────────────────────────────────────────────

import {
  useGlobalSearch,
  useSearchSuggestions,
  useSearchResults,
  useAddToSearchIndex,
  useRemoveFromSearchIndex,
  useRebuildSearchIndex,
} from '../../../src/hooks/useSearch';

// ──────────────────────────────────────────────────────────────────────────────
// Mocked module imports (for typed access to mocks)
// ──────────────────────────────────────────────────────────────────────────────

import { get, post, del } from '../../../src/api/client';

// ──────────────────────────────────────────────────────────────────────────────
// Type-only imports for test fixtures
// ──────────────────────────────────────────────────────────────────────────────

import type {
  SearchQuery,
  SearchResult,
  SearchResultList,
  TypeaheadResponse,
} from '../../../src/types/common';

// ──────────────────────────────────────────────────────────────────────────────
// Typed mock references
// ──────────────────────────────────────────────────────────────────────────────

const mockedGet = vi.mocked(get);
const mockedPost = vi.mocked(post);
const mockedDel = vi.mocked(del);

// ──────────────────────────────────────────────────────────────────────────────
// Const Enum Numeric Values
// ──────────────────────────────────────────────────────────────────────────────
// SearchType and SearchResultType are const enums in common.ts.
// With isolatedModules: true, const enums cannot be imported at runtime.
// Following the project convention (see useEntities.test.ts FIELD_TYPE_*)
// we define the numeric values directly.

/** SearchType.Contains = 0 — case-insensitive ILIKE per word */
const SEARCH_TYPE_CONTAINS = 0;
/** SearchType.Fts = 1 — PostgreSQL FTS with stemming */
const SEARCH_TYPE_FTS = 1;
/** SearchResultType.Compact = 0 — minimal fields (id, snippet, url) */
const RESULT_TYPE_COMPACT = 0;
/** SearchResultType.Full = 1 — all content fields included */
const RESULT_TYPE_FULL = 1;

// ══════════════════════════════════════════════════════════════════════════════
// Test Fixtures
// ══════════════════════════════════════════════════════════════════════════════

/**
 * Mock search query matching the C# SearchQuery from SearchManager.cs.
 * Exercises the Contains search path with entity and app filters.
 */
const mockSearchQuery: SearchQuery = {
  searchType: SEARCH_TYPE_CONTAINS as SearchQuery['searchType'],
  resultType: RESULT_TYPE_FULL as SearchQuery['resultType'],
  text: 'acme',
  entities: ['account-entity-guid', 'contact-entity-guid'],
  apps: ['crm-app-guid'],
  records: [],
  skip: 0,
  limit: 20,
};

/**
 * Mock FTS search query exercising the Fts search path.
 * In the monolith, this uses to_tsvector/to_tsquery with FtsAnalyzer stemming.
 */
const mockFtsSearchQuery: SearchQuery = {
  searchType: SEARCH_TYPE_FTS as SearchQuery['searchType'],
  resultType: RESULT_TYPE_FULL as SearchQuery['resultType'],
  text: 'technology company',
  entities: [],
  apps: [],
  records: [],
  skip: 0,
  limit: 20,
};

/**
 * Mock SearchResult matching the C# SearchResult mapped from system_search table.
 * Fields: id, entities, apps, records, content, stemContent, snippet, url, auxData, timestamp.
 */
const mockSearchResult: SearchResult = {
  id: 'result-guid-001',
  entities: ['account-entity-guid'],
  apps: ['crm-app-guid'],
  records: ['account-record-guid'],
  content: 'Acme Corporation - technology company in New York',
  stemContent: 'acm corpor technolog compani new york',
  snippet: 'Acme Corporation',
  url: '/crm/accounts/account-record-guid',
  auxData: '{"type":"account"}',
  timestamp: '2024-01-20T10:00:00Z',
};

/**
 * Second mock result for pagination/multi-result tests.
 */
const mockSearchResult2: SearchResult = {
  id: 'result-guid-002',
  entities: ['contact-entity-guid'],
  apps: ['crm-app-guid'],
  records: ['contact-record-guid'],
  content: 'Jane Doe - Acme Corporation contact',
  stemContent: 'jan doe acm corpor contact',
  snippet: 'Jane Doe',
  url: '/crm/contacts/contact-record-guid',
  auxData: '{"type":"contact"}',
  timestamp: '2024-01-20T09:30:00Z',
};

/**
 * Mock SearchResultList matching C# SearchResultList (extends List<SearchResult>).
 * Includes totalCount from the COUNT(*) OVER() windowed aggregate.
 */
const mockSearchResultList: SearchResultList = {
  results: [mockSearchResult],
  totalCount: 1,
};

/**
 * Mock multi-result list for pagination tests.
 */
const mockSearchResultListMulti: SearchResultList = {
  results: [mockSearchResult, mockSearchResult2],
  totalCount: 42,
};

/**
 * Mock empty result list for no-results tests.
 */
const mockEmptySearchResultList: SearchResultList = {
  results: [],
  totalCount: 0,
};

/**
 * Mock TypeaheadResponse matching C# TypeaheadResponse from TypeaheadResponse.cs.
 * Contains TypeaheadResponseRow items with id, iconName, color, text, entityName, fieldName.
 */
const mockTypeaheadResponse: TypeaheadResponse = {
  results: [
    {
      id: 'account-guid-001',
      iconName: 'fa fa-building',
      color: 'teal',
      text: 'Acme Corporation',
      entityName: 'account',
      fieldName: 'name',
    },
    {
      id: 'contact-guid-001',
      iconName: 'fa fa-user',
      color: 'blue',
      text: 'Acme Contact - John Smith',
      entityName: 'contact',
      fieldName: 'name',
    },
  ],
  pagination: { more: false },
};

/**
 * Mock empty typeahead response.
 */
const mockEmptyTypeaheadResponse: TypeaheadResponse = {
  results: [],
  pagination: { more: false },
};

// ══════════════════════════════════════════════════════════════════════════════
// Response Helpers
// ══════════════════════════════════════════════════════════════════════════════

/**
 * Build a successful API response envelope matching the ApiResponse<T> shape
 * from api/client.ts. The hooks unwrap response.object from this envelope.
 */
function createSuccessResponse<T>(object: T) {
  return {
    success: true as const,
    object,
    errors: [] as Array<{ key: string; value: string; message: string }>,
    statusCode: 200,
    timestamp: new Date().toISOString(),
    message: '',
    hash: 'response-hash',
  };
}

/**
 * Build an error response envelope for failure scenarios (400/403/409/500).
 * Mirrors the structure from ApiControllerBase.DoBadRequestResponse.
 */
function createErrorResponse(
  statusCode: number,
  errors: Array<{ key: string; value: string; message: string }>,
  message?: string,
) {
  return {
    success: false as const,
    object: undefined,
    errors,
    statusCode,
    timestamp: new Date().toISOString(),
    message: message || errors[0]?.message || 'Error',
    hash: undefined,
  };
}

// ══════════════════════════════════════════════════════════════════════════════
// QueryClient Wrapper
// ══════════════════════════════════════════════════════════════════════════════

let queryClient: QueryClient;

/**
 * Creates a React wrapper that provides QueryClientProvider context.
 * Uses React.createElement (not JSX) because the file is .ts not .tsx.
 */
function createWrapper() {
  return function Wrapper({ children }: { children: ReactNode }) {
    return React.createElement(
      QueryClientProvider,
      { client: queryClient },
      children,
    );
  };
}

// ══════════════════════════════════════════════════════════════════════════════
// Setup / Teardown
// ══════════════════════════════════════════════════════════════════════════════

beforeEach(() => {
  queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  vi.clearAllMocks();
});

afterEach(() => {
  queryClient.clear();
});

// #########################################################################
//  1. useGlobalSearch — Global search across entities
//     Replaces SearchManager.Search(SearchQuery) — Contains and FTS modes
//     with entity/app/record filters, pagination, windowed total count
// #########################################################################

describe('useGlobalSearch', () => {
  it('should execute contains search', async () => {
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse(mockSearchResultList),
    );

    const { result } = renderHook(
      () => useGlobalSearch(mockSearchQuery),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Verify POST /search called with the full SearchQuery payload
    expect(mockedPost).toHaveBeenCalledWith('/search', mockSearchQuery);
    expect(result.current.data).toBeDefined();
    expect(result.current.data!.results).toHaveLength(1);
    expect(result.current.data!.results[0].content).toBe(
      'Acme Corporation - technology company in New York',
    );
    expect(result.current.data!.results[0].snippet).toBe('Acme Corporation');
  });

  it('should execute FTS search', async () => {
    const ftsResultList: SearchResultList = {
      results: [mockSearchResult],
      totalCount: 1,
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(ftsResultList));

    const { result } = renderHook(
      () => useGlobalSearch(mockFtsSearchQuery),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Verify FTS query is sent with searchType = 1 (Fts)
    expect(mockedPost).toHaveBeenCalledWith('/search', mockFtsSearchQuery);
    expect(result.current.data!.results[0].stemContent).toBe(
      'acm corpor technolog compani new york',
    );
  });

  it('should filter by entities', async () => {
    const entityFilterQuery: SearchQuery = {
      ...mockSearchQuery,
      entities: ['account-entity-guid', 'contact-entity-guid'],
      apps: [],
      records: [],
    };
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse(mockSearchResultList),
    );

    const { result } = renderHook(
      () => useGlobalSearch(entityFilterQuery),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Verify entity filter is passed through to API
    const callArgs = mockedPost.mock.calls[0];
    expect(callArgs[0]).toBe('/search');
    expect((callArgs[1] as SearchQuery).entities).toEqual([
      'account-entity-guid',
      'contact-entity-guid',
    ]);
  });

  it('should filter by apps', async () => {
    const appFilterQuery: SearchQuery = {
      ...mockSearchQuery,
      entities: [],
      apps: ['crm-app-guid', 'project-app-guid'],
      records: [],
    };
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse(mockSearchResultList),
    );

    const { result } = renderHook(
      () => useGlobalSearch(appFilterQuery),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const callArgs = mockedPost.mock.calls[0];
    expect((callArgs[1] as SearchQuery).apps).toEqual([
      'crm-app-guid',
      'project-app-guid',
    ]);
  });

  it('should filter by records', async () => {
    const recordFilterQuery: SearchQuery = {
      ...mockSearchQuery,
      entities: [],
      apps: [],
      records: ['record-guid-001', 'record-guid-002'],
    };
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse(mockSearchResultList),
    );

    const { result } = renderHook(
      () => useGlobalSearch(recordFilterQuery),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const callArgs = mockedPost.mock.calls[0];
    expect((callArgs[1] as SearchQuery).records).toEqual([
      'record-guid-001',
      'record-guid-002',
    ]);
  });

  it('should handle pagination', async () => {
    const paginatedQuery: SearchQuery = {
      ...mockSearchQuery,
      skip: 20,
      limit: 10,
    };
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse(mockSearchResultListMulti),
    );

    const { result } = renderHook(
      () => useGlobalSearch(paginatedQuery),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Verify skip/limit are passed through (matching SearchManager OFFSET/LIMIT)
    const callArgs = mockedPost.mock.calls[0];
    expect((callArgs[1] as SearchQuery).skip).toBe(20);
    expect((callArgs[1] as SearchQuery).limit).toBe(10);
    expect(result.current.data!.results).toHaveLength(2);
  });

  it('should return totalCount', async () => {
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse(mockSearchResultListMulti),
    );

    const { result } = renderHook(
      () => useGlobalSearch(mockSearchQuery),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // totalCount mirrors the COUNT(*) OVER() windowed aggregate from SearchManager
    expect(result.current.data!.totalCount).toBe(42);
  });

  it('should use staleTime of 30 seconds', async () => {
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse(mockSearchResultList),
    );

    const { result } = renderHook(
      () => useGlobalSearch(mockSearchQuery),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // A second render with the same query within staleTime should reuse cached
    // data without issuing another network request (staleTime = 30_000 ms)
    renderHook(() => useGlobalSearch(mockSearchQuery), {
      wrapper: createWrapper(),
    });

    expect(mockedPost).toHaveBeenCalledTimes(1);
  });

  it('should not execute with empty query text', async () => {
    const emptyTextQuery: SearchQuery = {
      ...mockSearchQuery,
      text: '',
    };

    const { result } = renderHook(
      () => useGlobalSearch(emptyTextQuery),
      { wrapper: createWrapper() },
    );

    // When enabled is false, the query stays in pending status without fetching
    expect(result.current.isPending).toBe(true);
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedPost).not.toHaveBeenCalled();
  });

  it('should not execute with whitespace-only query text', async () => {
    const whitespaceQuery: SearchQuery = {
      ...mockSearchQuery,
      text: '   ',
    };

    const { result } = renderHook(
      () => useGlobalSearch(whitespaceQuery),
      { wrapper: createWrapper() },
    );

    expect(result.current.isPending).toBe(true);
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedPost).not.toHaveBeenCalled();
  });

  it('should handle no results', async () => {
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse(mockEmptySearchResultList),
    );

    const { result } = renderHook(
      () => useGlobalSearch(mockSearchQuery),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data!.results).toHaveLength(0);
    expect(result.current.data!.totalCount).toBe(0);
  });

  it('should handle null response object gracefully', async () => {
    // When the API returns success but with no object payload
    mockedPost.mockResolvedValueOnce({
      success: true,
      object: undefined,
      errors: [],
      statusCode: 200,
      timestamp: new Date().toISOString(),
      message: '',
    });

    const { result } = renderHook(
      () => useGlobalSearch(mockSearchQuery),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // The hook falls back to empty results when object is falsy
    expect(result.current.data!.results).toHaveLength(0);
    expect(result.current.data!.totalCount).toBe(0);
  });
});

// #########################################################################
//  2. useSearchSuggestions — Typeahead / Autocomplete
//     Replaces AppSearchService.cs stub + Contains-mode quick search
//     GET /v1/search/suggestions with q, entities, limit params
// #########################################################################

describe('useSearchSuggestions', () => {
  it('should fetch suggestions', async () => {
    mockedGet.mockResolvedValueOnce(
      createSuccessResponse(mockTypeaheadResponse),
    );

    const { result } = renderHook(
      () => useSearchSuggestions('acm'),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Verify GET /search/suggestions called with q param and default limit
    expect(mockedGet).toHaveBeenCalledWith('/search/suggestions', {
      q: 'acm',
      limit: 10,
    });
    expect(result.current.data!.results).toHaveLength(2);
    expect(result.current.data!.results[0].text).toBe('Acme Corporation');
    expect(result.current.data!.results[0].entityName).toBe('account');
  });

  it('should not fetch with fewer than 2 characters', async () => {
    const { result } = renderHook(
      () => useSearchSuggestions('a'),
      { wrapper: createWrapper() },
    );

    // Query stays pending/idle when text length < 2
    expect(result.current.isPending).toBe(true);
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should not fetch with empty text', async () => {
    const { result } = renderHook(
      () => useSearchSuggestions(''),
      { wrapper: createWrapper() },
    );

    expect(result.current.isPending).toBe(true);
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should not fetch with whitespace-only text under 2 chars trimmed', async () => {
    // Single char after trim
    const { result } = renderHook(
      () => useSearchSuggestions(' a'),
      { wrapper: createWrapper() },
    );

    expect(result.current.isPending).toBe(true);
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should use staleTime of 60 seconds', async () => {
    mockedGet.mockResolvedValueOnce(
      createSuccessResponse(mockTypeaheadResponse),
    );

    const { result } = renderHook(
      () => useSearchSuggestions('acme'),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Second render with same text within staleTime should reuse cache
    renderHook(() => useSearchSuggestions('acme'), {
      wrapper: createWrapper(),
    });

    expect(mockedGet).toHaveBeenCalledTimes(1);
  });

  it('should support entity type filter', async () => {
    mockedGet.mockResolvedValueOnce(
      createSuccessResponse(mockTypeaheadResponse),
    );

    const { result } = renderHook(
      () =>
        useSearchSuggestions('acm', {
          entities: ['account-entity-guid', 'contact-entity-guid'],
        }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Verify entities are passed as comma-separated string
    expect(mockedGet).toHaveBeenCalledWith('/search/suggestions', {
      q: 'acm',
      limit: 10,
      entities: 'account-entity-guid,contact-entity-guid',
    });
  });

  it('should support custom limit', async () => {
    mockedGet.mockResolvedValueOnce(
      createSuccessResponse(mockTypeaheadResponse),
    );

    const { result } = renderHook(
      () => useSearchSuggestions('acm', { limit: 5 }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/search/suggestions', {
      q: 'acm',
      limit: 5,
    });
  });

  it('should handle null response object gracefully', async () => {
    mockedGet.mockResolvedValueOnce({
      success: true,
      object: undefined,
      errors: [],
      statusCode: 200,
      timestamp: new Date().toISOString(),
      message: '',
    });

    const { result } = renderHook(
      () => useSearchSuggestions('acme'),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Falls back to empty results with pagination.more = false
    expect(result.current.data!.results).toHaveLength(0);
    expect(result.current.data!.pagination.more).toBe(false);
  });

  it('should fetch with exactly 2 characters', async () => {
    mockedGet.mockResolvedValueOnce(
      createSuccessResponse(mockTypeaheadResponse),
    );

    const { result } = renderHook(
      () => useSearchSuggestions('ac'),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledTimes(1);
    expect(mockedGet).toHaveBeenCalledWith('/search/suggestions', {
      q: 'ac',
      limit: 10,
    });
  });
});

// #########################################################################
//  3. useSearchResults — Retrieve cached search result set by ID
//     Used for paginating through server-cached search results
//     GET /v1/search/results/{searchId}
// #########################################################################

describe('useSearchResults', () => {
  it('should fetch cached search results', async () => {
    const searchId = 'cached-search-id-001';
    mockedGet.mockResolvedValueOnce(
      createSuccessResponse(mockSearchResultListMulti),
    );

    const { result } = renderHook(
      () => useSearchResults(searchId),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(`/search/results/${searchId}`);
    expect(result.current.data!.results).toHaveLength(2);
    expect(result.current.data!.totalCount).toBe(42);
  });

  it('should not fetch when searchId is undefined', async () => {
    const { result } = renderHook(
      () => useSearchResults(undefined),
      { wrapper: createWrapper() },
    );

    expect(result.current.isPending).toBe(true);
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should not fetch when searchId is empty string', async () => {
    const { result } = renderHook(
      () => useSearchResults(''),
      { wrapper: createWrapper() },
    );

    // Boolean('') is false, so enabled = false
    expect(result.current.isPending).toBe(true);
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should handle null response object gracefully', async () => {
    mockedGet.mockResolvedValueOnce({
      success: true,
      object: undefined,
      errors: [],
      statusCode: 200,
      timestamp: new Date().toISOString(),
      message: '',
    });

    const { result } = renderHook(
      () => useSearchResults('some-search-id'),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data!.results).toHaveLength(0);
    expect(result.current.data!.totalCount).toBe(0);
  });
});

// #########################################################################
//  4. useAddToSearchIndex — Add entry to search index (admin)
//     Replaces SearchManager.AddToIndex(url, snippet, content, entities,
//     apps, records, auxData, timestamp)
//     POST /v1/search/index
// #########################################################################

describe('useAddToSearchIndex', () => {
  it('should add entry to search index', async () => {
    const newEntry: SearchResult = {
      ...mockSearchResult,
      id: 'new-entry-guid',
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(newEntry));

    const { result } = renderHook(() => useAddToSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync({
        url: '/crm/accounts/account-record-guid',
        snippet: 'Acme Corporation',
        content: 'Acme Corporation - technology company in New York',
        entities: ['account-entity-guid'],
        apps: ['crm-app-guid'],
        records: ['account-record-guid'],
        auxData: '{"type":"account"}',
        timestamp: '2024-01-20T10:00:00Z',
      });
    });

    expect(mockedPost).toHaveBeenCalledWith('/search/index', {
      url: '/crm/accounts/account-record-guid',
      snippet: 'Acme Corporation',
      content: 'Acme Corporation - technology company in New York',
      entities: ['account-entity-guid'],
      apps: ['crm-app-guid'],
      records: ['account-record-guid'],
      auxData: '{"type":"account"}',
      timestamp: '2024-01-20T10:00:00Z',
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data!.id).toBe('new-entry-guid');
  });

  it('should invalidate search queries on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockSearchResult));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useAddToSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync({
        url: '/crm/accounts/test',
        snippet: 'Test entry',
        content: 'Test content for indexing',
      });
    });

    // Verify cache invalidation with the ['search'] root key
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['search'],
      }),
    );

    invalidateSpy.mockRestore();
  });

  it('should handle duplicate index entry (409 conflict)', async () => {
    mockedPost.mockRejectedValueOnce(
      createErrorResponse(409, [
        {
          key: 'url',
          value: '/crm/accounts/existing',
          message: 'Search index entry already exists for this URL',
        },
      ]),
    );

    const { result } = renderHook(() => useAddToSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      try {
        await result.current.mutateAsync({
          url: '/crm/accounts/existing',
          snippet: 'Duplicate entry',
          content: 'This entry already exists',
        });
      } catch {
        // Expected to throw
      }
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });

  it('should handle missing response object as error', async () => {
    mockedPost.mockResolvedValueOnce({
      success: true,
      object: undefined,
      errors: [],
      statusCode: 200,
      timestamp: new Date().toISOString(),
      message: 'No response body',
    });

    const { result } = renderHook(() => useAddToSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      try {
        await result.current.mutateAsync({
          url: '/test',
          snippet: 'Test',
          content: 'Test content',
        });
      } catch {
        // The hook throws when response.object is missing
      }
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

// #########################################################################
//  5. useRemoveFromSearchIndex — Remove entry from search index (admin)
//     Replaces SearchManager.RemoveFromIndex(Guid id)
//     DELETE /v1/search/index/{id}
// #########################################################################

describe('useRemoveFromSearchIndex', () => {
  it('should remove entry from search index', async () => {
    const entryId = 'result-guid-001';
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse(undefined),
    );

    const { result } = renderHook(() => useRemoveFromSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync(entryId);
    });

    // Verify DELETE /search/index/{id} with URL-encoded ID
    expect(mockedDel).toHaveBeenCalledWith(
      `/search/index/${encodeURIComponent(entryId)}`,
    );
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  });

  it('should invalidate search queries on success', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useRemoveFromSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync('entry-to-remove');
    });

    // Verify cache invalidation with the ['search'] root key
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['search'],
      }),
    );

    invalidateSpy.mockRestore();
  });

  it('should URL-encode special characters in entry ID', async () => {
    const specialId = 'entry/with:special+chars';
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useRemoveFromSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync(specialId);
    });

    expect(mockedDel).toHaveBeenCalledWith(
      `/search/index/${encodeURIComponent(specialId)}`,
    );
  });

  it('should handle not-found entry (404)', async () => {
    mockedDel.mockRejectedValueOnce(
      createErrorResponse(404, [
        {
          key: 'id',
          value: 'nonexistent-guid',
          message: 'Search index entry not found',
        },
      ]),
    );

    const { result } = renderHook(() => useRemoveFromSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      try {
        await result.current.mutateAsync('nonexistent-guid');
      } catch {
        // Expected to throw
      }
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

// #########################################################################
//  6. useRebuildSearchIndex — Trigger full index rebuild (admin)
//     Long-running background operation, returns immediately
//     POST /v1/search/rebuild
// #########################################################################

describe('useRebuildSearchIndex', () => {
  it('should trigger index rebuild', async () => {
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse(undefined),
    );

    const { result } = renderHook(() => useRebuildSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync();
    });

    expect(mockedPost).toHaveBeenCalledWith('/search/rebuild');
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  });

  it('should not invalidate cache on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useRebuildSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync();
    });

    // Rebuild is a long-running background task; no immediate cache invalidation
    // Individual queries will naturally refetch on their staleTime boundary
    expect(invalidateSpy).not.toHaveBeenCalled();

    invalidateSpy.mockRestore();
  });

  it('should handle admin-only access restriction (403)', async () => {
    mockedPost.mockRejectedValueOnce(
      createErrorResponse(403, [
        {
          key: 'authorization',
          value: '',
          message: 'Only administrators can rebuild the search index',
        },
      ]),
    );

    const { result } = renderHook(() => useRebuildSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      try {
        await result.current.mutateAsync();
      } catch {
        // Expected to throw
      }
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });

  it('should handle server error (500)', async () => {
    mockedPost.mockRejectedValueOnce(
      createErrorResponse(
        500,
        [
          {
            key: 'server',
            value: '',
            message: 'An internal error occurred during index rebuild',
          },
        ],
        'An internal error occurred!',
      ),
    );

    const { result } = renderHook(() => useRebuildSearchIndex(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      try {
        await result.current.mutateAsync();
      } catch {
        // Expected to throw
      }
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

