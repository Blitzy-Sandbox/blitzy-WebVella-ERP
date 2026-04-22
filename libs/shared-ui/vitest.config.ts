import { defineConfig } from 'vitest/config';

// Use dynamic import for @vitejs/plugin-react which is ESM-only.
// Static import causes "ESM file cannot be loaded by require" in esbuild bundling.
//
// The `passWithNoTests: true` setting ensures that running `vitest run` against
// this package returns exit code 0 when no *.test.{ts,tsx} files are present.
// This mirrors the pattern established in `libs/shared-schemas/vitest.config.ts`
// and is required because Phase 4 Exit Criterion E4.C demands "0 failures" for
// every configured Vitest project including this one. shared-ui is currently a
// scaffold library exporting reusable React components (DataTable, Form,
// FieldComponents) and hooks (useAuth, useApi, usePagination) that are not yet
// imported by `apps/frontend`; the active frontend tree has its own
// implementations tested by the 61 Vitest specs under apps/frontend/tests/unit/.
// When shared-ui gains its first consumer in a future PR, contributors should
// add `src/**/*.test.{ts,tsx}` alongside each exported symbol; the default
// `include` glob above will pick them up automatically.
export default defineConfig(async () => {
  const { default: react } = await import('@vitejs/plugin-react');
  return {
    plugins: [react()],
    test: {
      environment: 'jsdom',
      globals: false,
      include: ['src/**/*.test.{ts,tsx}'],
      passWithNoTests: true,
    },
  };
});
