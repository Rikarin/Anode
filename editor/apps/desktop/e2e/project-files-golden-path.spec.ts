import type { Page } from '@playwright/test';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

async function expectTracks(window: Page, tracks: number): Promise<void> {
  await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
    'aria-label',
    new RegExp(`on ${tracks} tracks?$`)
  );
}

function document(folder: string): { name: string; sampleRate: number; tracks: string[] } {
  return JSON.parse(readFileSync(join(folder, 'project.json'), 'utf8')) as {
    name: string;
    sampleRate: number;
    tracks: string[];
  };
}

/** Save As, Save a Copy, a change of sample rate, Import from Project and templates, on the real Server and disk. */
test('a project goes through templates, Save As, a copy, a new rate and an import', async ({ window }) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-project-files-'));
  const projects = join(scratch, 'projects');
  const menu = async (item: string): Promise<void> => {
    await window.getByTestId('project-menu').click();
    await window.getByTestId(item).click();
  };

  try {
    // A factory template from the chooser.
    await window.getByTestId('new-project-name').fill('Golden');
    await window.getByTestId('new-project-folder').fill(projects);
    await window.getByTestId('template-Songwriter').click();
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Golden');
    await expectTracks(window, 5);
    const original = join(projects, 'Golden.anode');

    // An unsaved edit, then a copy: it holds the edit, and the session stays.
    await window.getByTestId('add-track').click();
    await expectTracks(window, 6);
    await menu('save-copy');
    const copy = join(projects, 'Copy.anode');
    await window.getByTestId('save-as-path').fill(copy);
    await window.getByTestId('save-as-confirm').click();
    await expect.poll(() => existsSync(join(copy, 'project.json')), { timeout: 30_000 }).toBe(true);
    expect(document(copy).tracks).toHaveLength(6);
    expect(document(copy).name).toBe('Copy');
    await expect(window.getByTestId('project-name')).toHaveText('Golden');
    await expect(window.getByTestId('unsaved')).toBeVisible();
    expect(document(original).tracks).toHaveLength(5);

    // Save As: it goes on in the new package, saved; the original stays as it was.
    await menu('save-as');
    const second = join(projects, 'Second.anode');
    await window.getByTestId('save-as-path').fill(second);
    await window.getByTestId('save-as-confirm').click();
    await expect(window.getByTestId('project-name')).toHaveText('Second', { timeout: 30_000 });
    await expect(window.getByTestId('unsaved')).toBeHidden();
    expect(document(second).tracks).toHaveLength(6);
    expect(document(original).tracks).toHaveLength(5);
    expect(existsSync(join(second, 'Autosave'))).toBe(false);

    // A new sample rate, with the choice made plainly.
    await menu('sample-rate');
    const dialog = window.getByRole('dialog', { name: 'Change Sample Rate' });
    await dialog.getByText('96', { exact: true }).click();
    await expect(window.getByTestId('rate-mode-convert')).toBeChecked();
    await window.getByTestId('change-sample-rate').click();
    await expect(dialog).toBeHidden({ timeout: 30_000 });
    await expect.poll(() => document(second).sampleRate).toBe(96_000);
    expect(readdirSync(join(second, 'Backups')).length).toBeGreaterThan(0);

    // Tracks from the copy, with the aux their send reaches, as one undo step.
    await window.getByTestId('project-menu').click();
    await window.getByTestId('import-menu').hover();
    await window.getByTestId('import-project').click();
    await window.getByTestId('import-project-path').fill(copy);
    await window.getByTestId('import-project-read').click();
    await window.getByTestId('import-track-Vocal').check();
    await window.getByTestId('import-project-confirm').click();
    await expectTracks(window, 8);
    await window.keyboard.press('ControlOrMeta+z');
    await expectTracks(window, 6);

    // The project as a template of the user's, offered by the chooser.
    await menu('save-template-menu');
    await window.getByTestId('template-name').fill('My Setup');
    await window.getByTestId('save-template').click();
    await expect(window.getByRole('dialog', { name: 'Save as Template' })).toBeHidden({ timeout: 30_000 });
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
