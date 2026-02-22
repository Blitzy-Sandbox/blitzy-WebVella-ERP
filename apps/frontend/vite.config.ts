/// <reference types="vitest" />
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { nxViteTsPaths } from '@nx/vite/plugins/nx-tsconfig-paths.plugin';

/**
 * Vite 6 build configuration for the WebVella ERP React 19 SPA.
 *
 * This configuration replaces the ASP.NET Core MSBuild/Razor SDK build pipeline
 * previously defined in WebVella.Erp.Web.csproj (embedded file manifest, content
 * copy rules, Razor compilation) and the middleware pipeline from Startup.cs.
 *
 * Key responsibilities:
 * - Development server with HMR (port 4200) replacing Kestrel/IIS hosting
 * - Production build with code splitting for < 200KB gzipped per-route chunks
 * - Nx monorepo path alias resolution for @webvella-erp/* shared libraries
 * - Vitest integration for component/unit testing with jsdom environment
 * - VITE_API_URL environment variable for LocalStack / production API Gateway targeting
 *
 * @see AAP §0.4.1 — Target structure: apps/frontend/
 * @see AAP §0.8.2 — Performance: Vite build < 30s, per-route chunk < 200KB gzipped
 * @see AAP §0.8.6 — VITE_API_URL env variable for LocalStack API Gateway
 */
export default defineConfig({
  /**
   * Project root directory. Set to __dirname so Vite resolves relative paths
   * from the apps/frontend/ directory within the Nx monorepo.
   */
  root: __dirname,

  /**
   * Cache directory following Nx convention. Stored under the workspace root
   * node_modules to avoid polluting the project directory and to leverage
   * Nx's cache management.
   */
  cacheDir: '../../node_modules/.vite/apps/frontend',

  /**
   * Development server configuration.
   * Port 4200 is the Nx convention for the primary frontend app.
   * Host set to 'localhost' for security — bind to loopback only.
   */
  server: {
    port: 4200,
    host: 'localhost',
  },

  /**
   * Preview server configuration for testing production builds locally.
   * Port 4300 to avoid conflicts with the dev server on 4200.
   */
  preview: {
    port: 4300,
    host: 'localhost',
  },

  /**
   * Vite plugins:
   * 1. react() — Enables React 19 automatic JSX transform (react-jsx) and
   *    Fast Refresh for hot module replacement. Replaces the server-rendered
   *    Razor Pages pipeline from WebVella.Erp.Web.
   * 2. nxViteTsPaths() — Resolves TypeScript path aliases from tsconfig.base.json
   *    (e.g., @webvella-erp/shared-ui, @webvella-erp/shared-schemas,
   *    @webvella-erp/shared-utils, @webvella-erp/shared-cdk-constructs).
   *    Required for cross-library imports in the Nx monorepo.
   */
  plugins: [react(), nxViteTsPaths()],

  /**
   * Production build configuration.
   * Output to ../../dist/apps/frontend (Nx workspace dist convention).
   * Code splitting via manualChunks ensures per-route chunks stay under
   * the 200KB gzipped budget (AAP §0.8.2).
   */
  build: {
    outDir: '../../dist/apps/frontend',
    emptyOutDir: true,
    reportCompressedSize: true,
    commonjsOptions: {
      transformMixedEsModules: true,
    },
    rollupOptions: {
      output: {
        /**
         * Manual chunk splitting strategy for optimal loading performance:
         * - vendor: Core React runtime + routing (loaded on every page)
         * - query: TanStack Query for server state management (loaded on data pages)
         * - state: Zustand for client-side UI state (loaded when stores are accessed)
         *
         * This separation ensures that updating one library doesn't invalidate
         * the cache for the others, and keeps individual chunk sizes well under
         * the 200KB gzipped target.
         */
        manualChunks: {
          vendor: ['react', 'react-dom', 'react-router'],
          query: ['@tanstack/react-query'],
          state: ['zustand'],
        },
      },
    },
  },

  /**
   * Compile-time defines for environment variable injection.
   *
   * VITE_API_URL controls whether the frontend targets the LocalStack
   * API Gateway (http://localhost:4566) or a production API endpoint.
   * This value is baked into the bundle at build time via Vite's define
   * mechanism, ensuring no runtime environment variable access is needed
   * in the browser (pure static SPA requirement, AAP §0.8.1).
   *
   * Developers can override via .env.local or shell environment:
   *   VITE_API_URL=https://api.example.com npm run build
   */
  define: {
    'import.meta.env.VITE_API_URL': JSON.stringify(
      process.env.VITE_API_URL || 'http://localhost:4566'
    ),
  },

  /**
   * Vitest configuration (integrated into vite.config.ts).
   * Uses jsdom environment for React component testing with
   * @testing-library/react. Globals enabled for describe/it/expect
   * without explicit imports. Coverage reports written to the
   * workspace-level coverage directory following Nx conventions.
   */
  test: {
    watch: false,
    globals: true,
    environment: 'jsdom',
    include: ['src/**/*.{test,spec}.{js,mjs,cjs,ts,mts,cts,jsx,tsx}'],
    reporters: ['default'],
    coverage: {
      reportsDirectory: '../../coverage/apps/frontend',
      provider: 'v8',
    },
  },
});
