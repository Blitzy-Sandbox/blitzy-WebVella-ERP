/// <reference types="vitest" />
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
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
/**
 * Vite plugin that serves synthetic downloadable files at /download-file/{filename}.
 * Required for Playwright E2E tests that validate the browser's download event by
 * clicking `<a href="/download-file/name.txt" download="name.txt">`.
 *
 * The response includes Content-Disposition: attachment so the browser treats it
 * as a file download rather than inline navigation. The body is a short text payload
 * representing the file content.
 */
function downloadFilePlugin() {
  return {
    name: 'download-file-plugin',
    configureServer(server: { middlewares: { use: (fn: (req: { url?: string }, res: { setHeader: (k: string, v: string) => void; end: (b: string) => void }, next: () => void) => void) => void } }) {
      server.middlewares.use((req, res, next) => {
        const prefix = '/download-file/';
        if (req.url && req.url.startsWith(prefix)) {
          const filename = decodeURIComponent(req.url.slice(prefix.length).split('?')[0]);
          res.setHeader('Content-Type', 'application/octet-stream');
          res.setHeader('Content-Disposition', `attachment; filename="${filename}"`);
          res.end(`File content for ${filename}`);
          return;
        }
        next();
      });
    },
  };
}

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
   *
   * Proxy configuration forwards LocalStack API calls through the Vite
   * dev server to avoid CORS issues. Two proxy paths are defined:
   *   1. '/aws' — Cognito SDK calls proxied to LocalStack port 4566
   *   2. '/api' — API Gateway calls proxied with the correct Host header
   *
   * Production builds deploy as pure static assets behind API Gateway
   * (same-origin or CORS-configured), so proxies are dev-only.
   */
  server: {
    port: 4200,
    host: 'localhost',
    proxy: {
      // Proxy Cognito SDK calls to LocalStack (same origin, no CORS).
      // The `configure` callback strips browser-injected `Origin` and
      // `Referer` headers so LocalStack treats the request as a
      // non-CORS server-to-server call, avoiding a 403.
      '/aws': {
        target: 'http://localhost:4566',
        changeOrigin: true,
        rewrite: (path: string) => path.replace(/^\/aws/, ''),
        configure: (proxy) => {
          proxy.on('proxyReq', (proxyReq) => {
            proxyReq.removeHeader('origin');
            proxyReq.removeHeader('referer');
          });
        },
      },
      // Proxy S3 file downloads to LocalStack (same-origin, no CORS).
      // The download handler creates blob:// URLs from fetched S3 content;
      // this proxy allows the fetch to go through the Vite dev server so
      // the browser treats it as same-origin.
      '/s3-proxy': {
        target: 'http://localhost:4566',
        changeOrigin: true,
        rewrite: (path: string) => path.replace(/^\/s3-proxy/, ''),
        configure: (proxy) => {
          proxy.on('proxyReq', (proxyReq) => {
            proxyReq.removeHeader('origin');
            proxyReq.removeHeader('referer');
          });
          /* Inject Content-Disposition: attachment so the browser triggers
             a real download event (required for Playwright E2E capture). */
          proxy.on('proxyRes', (proxyRes) => {
            proxyRes.headers['content-disposition'] = 'attachment';
          });
        },
      },
      // Proxy API Gateway calls with correct Host header for routing.
      // Origin/Referer headers are also stripped for the same reason.
      '/api': {
        target: 'http://localhost:4566',
        changeOrigin: true,
        rewrite: (path: string) => {
          // Strip the /api prefix first
          let p = path.replace(/^\/api/, '');

          // ── Direct REST-style hook path rewrites (useRecords / useEntities) ──
          // TanStack Query hooks use clean paths like /entities/{name}/records
          // that differ from the /entity-management/* legacy paths.  These must
          // run BEFORE the /entity-management rewrites to avoid partial matches.
          // Order: most-specific patterns first.
          // /entities/{name}/records{suffix} → /record/{name}{suffix}
          p = p.replace(/\/v1\/entities\/([^/]+)\/records/, '/v1/record/$1');
          // /relations/{id}/records{suffix} → /record/relations/{id}{suffix}
          p = p.replace(/\/v1\/relations\/([^/]+)\/records/, '/v1/record/relations/$1');
          // /entities/{id}/fields{suffix} → /meta/entity/{id}/fields{suffix}
          p = p.replace(/\/v1\/entities\/([^/]+)\/fields/, '/v1/meta/entity/$1/fields');
          // /entities/{idOrName} → /meta/entity/{idOrName}
          p = p.replace(/\/v1\/entities\/([^/]+)/, '/v1/meta/entity/$1');
          // /entities (bare, no subpath) → /meta/entity
          p = p.replace(/\/v1\/entities(?=[?]|$)/, '/v1/meta/entity');
          // /relations/{idOrName} (non-record) → /meta/relation/{idOrName}
          p = p.replace(/\/v1\/relations\/([^/]+)/, '/v1/meta/relation/$1');
          // /relations (bare) → /meta/relation
          p = p.replace(/\/v1\/relations(?=[?]|$)/, '/v1/meta/relation');

          // ── Entity Management path rewrites ──
          // Order matters: more specific paths first to avoid partial matches.
          // /entity-management/relations/records → /record/relations (before /relations)
          p = p.replace(/\/v1\/entity-management\/relations\/records/, '/v1/record/relations');
          // /entity-management/entities → /meta/entity (proxy+ routes)
          p = p.replace(/\/v1\/entity-management\/entities/, '/v1/meta/entity');
          // /entity-management/relations → /meta/relation
          p = p.replace(/\/v1\/entity-management\/relations/, '/v1/meta/relation');
          // /entity-management/records → /record
          p = p.replace(/\/v1\/entity-management\/records/, '/v1/record');
          // /entity-management/query/eql → /eql
          p = p.replace(/\/v1\/entity-management\/query\/eql/, '/v1/eql');
          // datasource-select2 before datasource (more specific first)
          p = p.replace(/\/v1\/entity-management\/query\/datasource-select2/, '/v1/datasource/select2');
          p = p.replace(/\/v1\/entity-management\/query\/datasource/, '/v1/datasource/execute');
          // /entity-management/search → /search
          p = p.replace(/\/v1\/entity-management\/search/, '/v1/search');
          // ── File Management ──
          // user-files before files (more specific first)
          p = p.replace(/\/v1\/file-management\/user-files/, '/v1/files/user-files');
          p = p.replace(/\/v1\/file-management\/files/, '/v1/files');
          // ── Plugin System ──
          p = p.replace(/\/v1\/plugin-system\/plugins/, '/v1/plugins');
          // ── Identity (users/roles) ──
          // /identity/users/me → /auth/me (before general /identity/users)
          p = p.replace(/\/v1\/identity\/users\/me/, '/v1/auth/me');
          p = p.replace(/\/v1\/identity\/users/, '/v1/users');
          p = p.replace(/\/v1\/identity\/roles/, '/v1/roles');
          // ── Workflow (singular → plural) ──
          p = p.replace(/\/v1\/workflow\//, '/v1/workflows/');
          // ── Reporting → Reports ──
          p = p.replace(/\/v1\/reporting\//, '/v1/reports/');
          return p;
        },
        configure: (proxy) => {
          proxy.on('proxyReq', (proxyReq) => {
            proxyReq.setHeader(
              'Host',
              `${process.env.VITE_API_GATEWAY_ID || 'c7c5a2ed'}.execute-api.localhost.localstack.cloud`,
            );
            proxyReq.removeHeader('origin');
            proxyReq.removeHeader('referer');
          });
        },
      },
    },
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
  plugins: [react() as never, tailwindcss() as never, nxViteTsPaths() as never, downloadFilePlugin() as never],

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
  /**
   * NOTE: Removed the previous `define` block that hard-coded
   * `import.meta.env.VITE_API_URL` from `process.env`. This caused
   * conflicts with `.env.local` values because `define` replaces the
   * literal string at compile time, overriding Vite's built-in
   * `.env.local` loading. All VITE_* variables now come exclusively
   * from `.env.local` or shell environment — Vite handles this natively.
   */

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
    include: [
      'src/**/*.{test,spec}.{js,mjs,cjs,ts,mts,cts,jsx,tsx}',
      'tests/unit/**/*.{test,spec}.{js,mjs,cjs,ts,mts,cts,jsx,tsx}',
    ],
    reporters: ['default'],
    coverage: {
      reportsDirectory: '../../coverage/apps/frontend',
      provider: 'v8',
    },
  },
});
