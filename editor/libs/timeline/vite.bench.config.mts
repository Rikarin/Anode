import { defineConfig } from 'vite';

/** The S1 rendering bench page (bench/index.html): `nx run timeline:bench` serves it, `bench-run` measures it. */
// Cross-origin isolation: performance.now() gets 5 µs resolution instead of 100 µs (Chromium) or 1 ms (WebKit).
const headers = { 'Cross-Origin-Opener-Policy': 'same-origin', 'Cross-Origin-Embedder-Policy': 'require-corp' };

export default defineConfig({
  root: `${import.meta.dirname}/bench`,
  base: './',
  cacheDir: `${import.meta.dirname}/../../node_modules/.vite/libs/timeline-bench`,
  server: { port: 4510, strictPort: true, headers },
  preview: { port: 4510, strictPort: true, headers },
  worker: { format: 'es' },
  build: {
    outDir: `${import.meta.dirname}/../../dist/bench/timeline`,
    emptyOutDir: true,
    target: 'es2022'
  }
});
