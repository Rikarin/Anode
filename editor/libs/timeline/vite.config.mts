/// <reference types='vitest' />
import { defineConfig } from 'vite';

export default defineConfig(() => ({
  root: import.meta.dirname,
  cacheDir: '../../node_modules/.vite/libs/timeline',
  test: {
    name: 'timeline',
    watch: false,
    environment: 'node',
    globals: true,
    include: ['src/**/*.spec.ts'],
    reporters: ['default']
  }
}));
