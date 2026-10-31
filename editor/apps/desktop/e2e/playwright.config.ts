import { defineConfig } from '@playwright/test';

/** Golden paths through the real desktop app: Electron, the built editor, and the engine sidecars (null audio). */
export default defineConfig({
  testDir: '.',
  globalSetup: './global-setup.ts',
  timeout: 120_000,
  expect: { timeout: 15_000 },
  workers: 1,
  reporter: process.env['CI']
    ? [['github'], ['html', { open: 'never', outputFolder: '../../../dist/e2e-report' }]]
    : 'list',
  use: { trace: 'retain-on-failure' },
  outputDir: '../../../dist/e2e-results'
});
