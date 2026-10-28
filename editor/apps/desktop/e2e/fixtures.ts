import { type ElectronApplication, type Page, test as base, _electron as electron, expect } from '@playwright/test';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const main = resolve(__dirname, '../../../dist/apps/desktop/main.js');
const TestClapFolder = resolve(__dirname, '../../../../engine/artifacts/native/clap');
const TestVst3Folder = resolve(__dirname, '../../../../engine/artifacts/native/vst3');

/**
 * Each test gets a fresh app: throwaway settings and data, its own sidecars (null audio). `desktopSettings` starts it
 * with that settings.json instead of the defaults (a remote Server, say).
 */
export const test = base.extend<{
  desktopSettings: object | null;
  app: ElectronApplication;
  window: Page;
}>({
  desktopSettings: [null, { option: true }],
  app: async ({ desktopSettings }, use) => {
    const scratch = mkdtempSync(join(tmpdir(), 'anode-e2e-'));
    if (desktopSettings) {
      mkdirSync(join(scratch, 'user-data'), { recursive: true });
      writeFileSync(join(scratch, 'user-data', 'settings.json'), JSON.stringify(desktopSettings));
    }

    const app = await electron.launch({
      args: [main],
      env: {
        ...process.env,
        ANODE_USER_DATA: join(scratch, 'user-data'),
        ANODE_DATA_DIR: join(scratch, 'server-data'),
        // The engine's test CLAP instrument (engine/native/an_testclap), so the golden paths can load a CLAP plugin.
        CLAP_PATH: TestClapFolder,
        // The engine's test VST3 effects (engine/native/an_testvst3), hosted in the plugin sandbox.
        VST3_PATH: TestVst3Folder,
        // A virtual MIDI output, so external MIDI tracks have a port on any machine.
        ANODE_VIRTUAL_MIDI: '1'
      }
    });
    await use(app);
    for (const page of app.windows()) {
      page.on('dialog', dialog => void dialog.accept().catch(() => undefined));
    }

    await app.close().catch(() => undefined); // a test may have killed it
    rmSync(scratch, { recursive: true, force: true });
  },
  window: async ({ app }, use) => {
    const window = await app.firstWindow();
    // First start: the sidecars have to come up and the editor has to connect.
    await expect(window.getByRole('toolbar', { name: 'Transport' })).toBeVisible({ timeout: 90_000 });
    await use(window);
  }
});

export { expect };
