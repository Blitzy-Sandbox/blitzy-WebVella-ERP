/**
 * Global Search TanStack Query Hooks
 *
 * Provides 6 hooks for the Entity Management microservice's search endpoints,
 * replacing the monolith's `SearchManager.cs` (PostgreSQL FTS-based
 * `system_search` table CRUD) and the stub `AppSearchService.cs`.
 *
 * Architecture mapping:
 *   - SearchManager.Search(SearchQuery)     → useGlobalSearch  (POST /v1/search)
 *   - SearchManager.AddToIndex(...)         → useAddToSearchIndex  (POST /v1/search/index)
 *   - SearchManager.RemoveFromIndex(id)     → useRemoveFromSearchIndex  (DELETE /v1/search/index/{id})
 *   - AppSearchService (stub)               → useSearchSuggestions  (GET /v1/search/suggestions)
 *   - Paginated result retrieval            → useSearchResults  (GET /v1/search/results/{searchId})
 *   - Admin full rebuild                    → useRebuildSearchIndex  (POST /v1/search/rebuild)
 *
 * Design decisions:
 *   - Debouncing is handled at the component level, NOT inside these hooks.
 *   - Query keys follow a factory pattern for consistent cache invalidation.
 *   - staleTime values reflect freshness requirements:
 *       30 s for full search results (mirrors SearchManager.Search 60 s cmd timeout / 2)
 *       60 s for typeahead suggestions (less time-sensitive)
 *   - Admin-only index management hooks are separated from user-facing search.
 *
 * @module hooks/useSearch
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import * as client from '../api/client';
import type {
  SearchQuery,
  SearchResult,
  SearchResultList,
  TypeaheadResponse,
  BaseResponseModel,
} from '../types/common';

// ---------------------------------------------------------------------------
// Query Key Factory
// ---------------------------------------------------------------------------

/**
 * Centralised query key factory for all search-related TanStack Query caches.
 *
 * Pattern:
 *   searchKeys.all           → invalidates every search-related cache
 *   searchKeys.global(query) → per-query cache for global search
 *   searchKeys.suggestions   → per-text cache for typeahead
 *   searchKeys.results       → per-searchId cache for paginated results
 */
const searchKeys = {
  /** Root key used to invalidate all search caches at once. */
  all: ['search'] as const,

  /** Key for a specific global search query. */
  global: (query: SearchQuery) => ['search', query] as const,

  /** Key for typeahead suggestions scoped by input text and options. */
  suggestions: (text: string, options?: SearchSuggestionsOptions) =>
    ['search-suggestions', text, options] as const,

  /** Key for a cached search result set identified by its server-side ID. */
  results: (searchId: string) => ['search-results', searchId] as const,
};

// ---------------------------------------------------------------------------
// Local Types (not exported — consumers infer via function signatures)
// ---------------------------------------------------------------------------

/**
 * Options for the typeahead / autocomplete suggestions hook.
 *
 * Mirrors the query-string parameters accepted by `GET /v1/search/suggestions`.
 */
interface SearchSuggestionsOptions {
  /** Filter suggestions to these entity IDs (GUIDs). */
  entities?: string[];
  /** Maximum number of suggestions to return (default: 10). */
  limit?: number;
}

/**
 * Payload for adding an entry to the search index.
 *
 * Mirrors the C# `SearchManager.AddToIndex` parameter list:
 *   AddToIndex(url, snippet, content, entities, apps, records, auxData, timestamp)
 */
interface AddToSearchIndexPayload {
  /** Deep-link URL to the record being indexed. */
  url: string;
  /** Short text snippet shown in search results. */
  snippet: string;
  /** Full content to be indexed for text matching. */
  content: string;
  /** Entity IDs (GUIDs) associated with this entry. */
  entities?: string[];
  /** Application IDs (GUIDs) associated with this entry. */
  apps?: string[];
  /** Record IDs (GUIDs) associated with this entry. */
  records?: string[];
  /** Auxiliary data stored alongside the search entry. */
  auxData?: string;
  /** ISO 8601 timestamp; defaults to server UTC now if omitted. */
  timestamp?: string;
}

// ---------------------------------------------------------------------------
// Query Hooks
// ---------------------------------------------------------------------------

/**
 * Executes a global search across entities via the Entity Management service.
 *
 * Replaces `SearchManager.Search(SearchQuery)` which performed:
 *   - Contains mode: case-insensitive ILIKE `%word%` per word against `content`
 *   - FTS mode: `to_tsvector('simple', stem_content) @@ to_tsquery('simple', …)`
 *   - Filters by entities, apps, records lists
 *   - Pagination via skip / limit
 *   - ORDER BY timestamp DESC (non-FTS) or relevance (FTS)
 *
 * In the target architecture the Entity Management service's DynamoDB GSI
 * handles indexing and the Lambda handler translates SearchQuery into the
 * appropriate query operations.
 *
 * @param query - Search parameters (searchType, resultType, text, entities, apps, records, skip, limit)
 * @returns TanStack Query result wrapping `SearchResultList` (results + totalCount)
 *
 * @example
 * ```tsx
 * const { data, isLoading, error } = useGlobalSearch({
 *   searchType: SearchType.Contains,
 *   resultType: SearchResultType.Compact,
 *   text: 'invoice',
 *   entities: [],
 *   apps: [],
 *   records: [],
 *   skip: 0,
 *   limit: 20,
 * });
 * ```
 */
export function useGlobalSearch(query: SearchQuery) {
  return useQuery({
    queryKey: searchKeys.global(query),
    queryFn: async (): Promise<SearchResultList> => {
      const response = await client.post<SearchResultList>('/search', query);
      // The API envelope wraps the payload in `object`; unwrap for consumers
      if (!response.object) {
        return { results: [], totalCount: 0 };
      }
      return response.object;
    },
    // Only execute when the user has typed something meaningful
    enabled: query.text.trim().length > 0,
    // 30 seconds — search results should be relatively fresh but avoid
    // hammering the API on rapid re-renders
    staleTime: 30_000,
  });
}

/**
 * Fetches typeahead / autocomplete suggestions for the search input.
 *
 * Replaces the functionality that `AppSearchService.cs` was reserved for
 * (currently a stub marked `//TO BE DEVELOPED`) plus the suggestion-style
 * querying that `SearchManager.Search` handled with `SearchType.Contains`.
 *
 * The hook is disabled until the user types at least 2 characters to avoid
 * sending overly broad queries.
 *
 * @param text    - Current search input text
 * @param options - Optional filters (entity IDs, result limit)
 * @returns TanStack Query result wrapping `TypeaheadResponse`
 *
 * @example
 * ```tsx
 * const { data } = useSearchSuggestions(inputValue, {
 *   entities: ['some-entity-id'],
 *   limit: 5,
 * });
 * ```
 */
export function useSearchSuggestions(
  text: string,
  options?: SearchSuggestionsOptions,
) {
  return useQuery({
    queryKey: searchKeys.suggestions(text, options),
    queryFn: async (): Promise<TypeaheadResponse> => {
      // Build query-string parameters matching the Lambda handler contract
      const params: Record<string, unknown> = {
        q: text,
        limit: options?.limit ?? 10,
      };

      // Pass entity filter as comma-separated string
      if (options?.entities && options.entities.length > 0) {
        params.entities = options.entities.join(',');
      }

      const response = await client.get<TypeaheadResponse>(
        '/search/suggestions',
        params,
      );

      if (!response.object) {
        return { results: [], pagination: { more: false } };
      }
      return response.object;
    },
    // Require at least 2 characters before firing the request
    enabled: text.trim().length >= 2,
    // 60 seconds — suggestions are less time-sensitive than full results
    staleTime: 60_000,
  });
}

/**
 * Retrieves a previously executed search result set by its server-side ID.
 *
 * Used for paginating through cached search results without re-executing the
 * underlying query. The search ID is returned by the initial `POST /v1/search`
 * call and identifies the result set on the server.
 *
 * @param searchId - Server-assigned search result set identifier; undefined disables the query
 * @returns TanStack Query result wrapping `SearchResultList`
 *
 * @example
 * ```tsx
 * const { data } = useSearchResults(cachedSearchId);
 * ```
 */
export function useSearchResults(searchId?: string) {
  return useQuery({
    queryKey: searchKeys.results(searchId ?? ''),
    queryFn: async (): Promise<SearchResultList> => {
      const response = await client.get<SearchResultList>(
        `/search/results/${searchId}`,
      );

      if (!response.object) {
        return { results: [], totalCount: 0 };
      }
      return response.object;
    },
    // Only fire when a valid searchId is provided
    enabled: Boolean(searchId),
  });
}

// ---------------------------------------------------------------------------
// Mutation Hooks (Admin Operations)
// ---------------------------------------------------------------------------

/**
 * Adds a new entry to the search index (admin operation).
 *
 * Replaces `SearchManager.AddToIndex(url, snippet, content, entities, apps,
 * records, auxData, timestamp)` which:
 *   1. Built a `SearchResult` with a new GUID, lowercased content, FTS-stemmed
 *      `stemContent` via `FtsAnalyzer.ProcessText`, and UTC timestamp.
 *   2. Inserted a row into the `system_search` PostgreSQL table.
 *
 * In the target architecture the Entity Management Lambda handler accepts the
 * same payload, computes stemming server-side, and writes to DynamoDB.
 *
 * On success, all `['search']`-prefixed query caches are invalidated so that
 * subsequent search results reflect the newly indexed entry.
 *
 * @returns TanStack Query mutation exposing `mutate(payload)` / `mutateAsync(payload)`
 *
 * @example
 * ```tsx
 * const addMutation = useAddToSearchIndex();
 * addMutation.mutate({
 *   url: '/app/crm/accounts/abc-123',
 *   snippet: 'Acme Corp — Premium account',
 *   content: 'Acme Corp premium account contact@acme.com',
 * });
 * ```
 */
export function useAddToSearchIndex() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (payload: AddToSearchIndexPayload): Promise<SearchResult> => {
      const response = await client.post<SearchResult>('/search/index', payload);
      if (!response.object) {
        // Server acknowledged but returned no object — construct a minimal result
        // This should not happen in normal operation
        throw new Error(response.message || 'Failed to add entry to search index');
      }
      return response.object;
    },
    onSuccess: () => {
      // Invalidate all search caches so results include the new entry
      queryClient.invalidateQueries({ queryKey: searchKeys.all });
    },
  });
}

/**
 * Removes an entry from the search index by ID (admin operation).
 *
 * Replaces `SearchManager.RemoveFromIndex(Guid id)` which executed:
 *   `DELETE FROM system_search WHERE id = @id`
 *
 * On success, all `['search']`-prefixed query caches are invalidated so that
 * stale references to the removed entry are purged.
 *
 * @returns TanStack Query mutation exposing `mutate(id)` / `mutateAsync(id)`
 *
 * @example
 * ```tsx
 * const removeMutation = useRemoveFromSearchIndex();
 * removeMutation.mutate('some-search-entry-guid');
 * ```
 */
export function useRemoveFromSearchIndex() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: string) => {
      const response = await client.del<BaseResponseModel>(
        `/search/index/${encodeURIComponent(id)}`,
      );
      return response;
    },
    onSuccess: () => {
      // Invalidate all search caches so removed entries disappear from results
      queryClient.invalidateQueries({ queryKey: searchKeys.all });
    },
  });
}

/**
 * Triggers a full rebuild of the search index (admin operation).
 *
 * This is a heavy, long-running background operation: the server re-indexes
 * every entity/record in the system. The Lambda handler kicks off the process
 * asynchronously (via Step Functions or an SQS message) and returns
 * immediately.
 *
 * No query cache invalidation is performed here because:
 *   1. The rebuild is asynchronous — data is not immediately available.
 *   2. Individual search queries will naturally refetch on their next staleTime
 *      boundary.
 *
 * @returns TanStack Query mutation exposing `mutate()` / `mutateAsync()`
 *
 * @example
 * ```tsx
 * const rebuildMutation = useRebuildSearchIndex();
 * rebuildMutation.mutate();
 * ```
 */
export function useRebuildSearchIndex() {
  return useMutation({
    mutationFn: async () => {
      const response = await client.post<BaseResponseModel>('/search/rebuild');
      return response;
    },
    // No onSuccess cache invalidation — rebuild is a long-running background task
  });
}
