/**
 * apps/frontend/src/main.tsx
 *
 * React 19 application entry point for the WebVella ERP SPA.
 *
 * Replaces the monolith's bootstrap chain:
 * - `Program.cs`  → WebHost.CreateDefaultBuilder(args).UseStartup<Startup>().Build().Run()
 * - `Startup.cs`  → DI container composition + HTTP middleware pipeline
 *
 * Provider stack (outer → inner):
 *   React.StrictMode       — Dev-time safety checks and double-render diagnostics
 *                             (replaces env.IsDevelopment() conditional error pages)
 *   QueryClientProvider    — TanStack Query server-state management
 *                             (replaces ErpRequestContext, PageDataModel data fetching,
 *                              PageService caching, DataSourceManager execution)
 *   App                    — BrowserRouter + AuthProvider + AppRouter
 *                             (replaces MapRazorPages, MapControllerRoute,
 *                              AddAuthentication, UseErpMiddleware, UseJwtMiddleware)
 *
 * This is a pure static SPA — zero server-side rendering, zero Lambda@Edge,
 * zero server components. The built output is deployed as static assets to S3.
 */

import React from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import App from './App';

/**
 * TanStack Query client configured for the WebVella ERP application.
 *
 * Configuration rationale:
 * - `staleTime: 300_000` (5 minutes) — mirrors the monolith's entity metadata
 *   cache invalidation interval from `Cache.cs`, which used IMemoryCache with
 *   a 5-minute sliding expiration for entity and relation metadata.
 * - `retry: 1` — single retry for transient network failures. The monolith's
 *   NpgsqlConnection had built-in retry; a single HTTP retry provides similar
 *   resilience without aggressive polling.
 * - `refetchOnWindowFocus: false` — prevents unnecessary API calls when the
 *   user switches browser tabs. The monolith served server-rendered pages that
 *   did not refetch on focus; this preserves that behaviour.
 */
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5 * 60 * 1000,
      retry: 1,
      refetchOnWindowFocus: false,
    },
  },
});

/**
 * Obtain the root DOM element defined in `index.html`.
 *
 * The `<div id="root"></div>` element is the mount target for the entire
 * React component tree. A non-null assertion is used because the element
 * is guaranteed to exist in the static `index.html` served by Vite (dev)
 * or S3 (production).
 */
const rootElement = document.getElementById('root');

if (!rootElement) {
  throw new Error(
    'Root element not found. Ensure index.html contains <div id="root"></div>.'
  );
}

/**
 * Create a React 19 concurrent-mode root and render the application tree.
 *
 * `createRoot` replaces the legacy `ReactDOM.render` API and enables:
 * - Concurrent features (automatic batching, transitions, Suspense)
 * - Improved hydration and streaming (not used here — pure client SPA)
 *
 * This mirrors `Program.cs` → `BuildWebHost(args).Run()` which bootstraps
 * the ASP.NET Core host process.
 */
const root = createRoot(rootElement);

root.render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>
  </React.StrictMode>
);
