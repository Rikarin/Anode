import { defineConfig } from '@playwright/test';

/** Golden paths through the real desktop app: Electron, the built editor, and the engine sidecars (null audio). */
export default defineConfig({
  testDir: '.',
  // Not on Linux yet (#49): DNS-SD, virtual MIDI ports and these three's UI steps. macOS runs them all.
  testIgnore:
    process.platform === 'linux'
      ? [
          'advanced-audio-golden-path.spec.ts',
          'arranging-golden-path.spec.ts',
          'loops-performance-golden-path.spec.ts',
          'phase8/**',
          'phase9/**',
          'phase10/**'
        ]
      : [],
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
