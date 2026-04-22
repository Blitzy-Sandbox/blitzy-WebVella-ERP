/// <reference types="vitest" />
/**
 * Vitest configuration for the WebVella ERP React 19 SPA.
 *
 * Separated from vite.config.ts because @tailwindcss/vite v4 is ESM-only
 * and cannot be loaded by the CJS esbuild bundler embedded in vitest 2.x.
 * This file contains ONLY the settings required for unit / component testing
 * and omits the Tailwind CSS Vite plugin that is only needed at build / dev time.
 *
 * @see vite.config.ts for the full build configuration
 */
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { nxViteTsPaths } from '@nx/vite/plugins/nx-tsconfig-paths.plugin';

export default defineConfig({
  root: __dirname,
  cacheDir: '../../node_modules/.vite/apps/frontend',
  // eslint-disable-next-line @typescript-eslint/no-explicit-any -- vitest bundles its own vite types which differ from the root vite package
  plugins: [react(), nxViteTsPaths()] as any,
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
