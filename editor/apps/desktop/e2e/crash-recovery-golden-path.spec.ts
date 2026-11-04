import { type ElectronApplication, type Page, _electron as electron } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { expect, test } from './fixtures';

const main = resolve(__dirname, '../../../dist/apps/desktop/main.js');

async function launch(scratch: string): Promise<{ app: ElectronApplication; window: Page }> {
  const app = await electron.launch({
    args: [main],
    env: {
      ...process.env,
      ANODE_USER_DATA: join(scratch, 'user-data'),
      ANODE_DATA_DIR: join(scratch, 'server-data'),
      Anode__AutosaveSeconds: '0.2'
    }
  });
  const window = await app.firstWindow();
  await expect(window.getByRole('toolbar', { name: 'Transport' })).toBeVisible({ timeout: 90_000 });
  return { app, window };
}

/** Kills the app and its Server at once, with no shutdown: only what is on disk by now survives. */
function killEverything(app: ElectronApplication): void {
  const electronPid = app.process().pid ?? 0;
  const children = execFileSync('pgrep', ['-P', String(electronPid)], { encoding: 'utf8' })
    .trim()
    .split('\n');
  const server = children.filter(pid => {
    const command = execFileSync('ps', ['-o', 'command=', '-p', pid], { encoding: 'utf8' });
    return /anode-server|Anode\.Server/i.test(command);
  });
  expect(server).toHaveLength(1);
  for (const pid of [...server, String(electronPid)]) {
    process.kill(Number(pid), 'SIGKILL');
  }
}

async function expectTracks(window: Page, tracks: number): Promise<void> {
  await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
    'aria-label',
    new RegExp(`on ${tracks} tracks?$`)
  );
}

// docs/04-phase-3.md, "Autosave and crash recovery", against the real app: edits made after a save survive the app and
// its Server being killed, the next launch offers them, and the save stays as it was until they are saved.
test('recovers edits autosaved before the app was killed', async () => {
  test.skip(process.platform === 'win32', 'Finds the Server with pgrep.');
  const scratch = mkdtempSync(join(tmpdir(), 'anode-recovery-'));
  const projects = join(scratch, 'projects');
  const folder = join(projects, 'Crash.anode');
  try {
    const first = await launch(scratch);
    await first.window.getByTestId('new-project-name').fill('Crash');
    await first.window.getByTestId('new-project-folder').fill(projects);
    await first.window.getByTestId('create-project').click();
    await expect(first.window.getByTestId('project-name')).toHaveText('Crash');
    await first.window.getByTestId('add-track').click();
    await expectTracks(first.window, 1);
    await expect(first.window.getByTestId('unsaved')).toBeVisible();
    await first.window.keyboard.press('ControlOrMeta+s');
    await expect(first.window.getByTestId('unsaved')).toBeHidden();
    const saved = readFileSync(join(folder, 'project.json'), 'utf8');

    await first.window.getByTestId('add-track').click();
    await expectTracks(first.window, 2);
    await expect(first.window.getByTestId('unsaved')).toHaveAttribute('title', /autosaved/);

    killEverything(first.app);
    await first.app.close().catch(() => undefined);

    const second = await launch(scratch);
    await expect(second.window.getByTestId('recovery')).toContainText('Crash');
    expect(readFileSync(join(folder, 'project.json'), 'utf8')).toBe(saved);

    await second.window.getByTestId('recover-Crash').click();
    await expect(second.window.getByTestId('project-name')).toHaveText('Crash');
    await expectTracks(second.window, 2);
    await expect(second.window.getByTestId('unsaved')).toBeVisible();
    expect(readFileSync(join(folder, 'project.json'), 'utf8')).toBe(saved);

    await second.window.keyboard.press('ControlOrMeta+s');
    await expect(second.window.getByTestId('unsaved')).toBeHidden();
    await second.app.close();
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
