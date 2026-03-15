/**
 * Type declaration shim for @tailwindcss/vite v4.
 *
 * @tailwindcss/vite v4+ ships ESM-only type declarations (.d.mts) which
 * cannot be resolved by TypeScript under `moduleResolution: "node"`.
 * This ambient module declaration bridges the gap so that vite.config.ts
 * type-checks without changing the global moduleResolution strategy.
 *
 * @see https://github.com/tailwindlabs/tailwindcss/issues/15044
 */
declare module '@tailwindcss/vite' {
  import type { Plugin } from 'vite';
  export default function tailwindcss(): Plugin;
}
