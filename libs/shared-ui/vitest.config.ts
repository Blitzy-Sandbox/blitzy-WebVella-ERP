import { defineConfig } from 'vitest/config';

// Use dynamic import for @vitejs/plugin-react which is ESM-only.
// Static import causes "ESM file cannot be loaded by require" in esbuild bundling.
export default defineConfig(async () => {
  const { default: react } = await import('@vitejs/plugin-react');
  return {
    plugins: [react()],
    test: {
      environment: 'jsdom',
      globals: false,
      include: ['src/**/*.test.{ts,tsx}'],
    },
  };
});
