/// <reference types='vitest' />
import { defineConfig } from 'vite';

export default defineConfig(() => ({
  root: import.meta.dirname,
  cacheDir: '../../node_modules/.vite/libs/notation',
  test: {
    name: 'notation',
    watch: false,
    environment: 'node',
    globals: true,
    include: ['src/**/*.spec.ts'],
    reporters: ['default']
  }
}));
