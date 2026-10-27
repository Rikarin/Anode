import { type ElectronApplication, type Page, test as base, _electron as electron, expect } from '@playwright/test';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const main = resolve(__dirname, '../../../dist/apps/desktop/main.js');

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
        ANODE_DATA_DIR: join(scratch, 'server-data')
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
