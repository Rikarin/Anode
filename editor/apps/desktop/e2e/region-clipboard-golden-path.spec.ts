import type { Page } from '@playwright/test';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

/** A second of silence as a mono 16-bit WAV at 48 kHz. */
function silence(): Buffer {
  const data = Buffer.alloc(48_000 * 2);
  const header = Buffer.alloc(44);
  header.write('RIFF', 0);
  header.writeUInt32LE(36 + data.length, 4);
  header.write('WAVEfmt ', 8);
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20);
  header.writeUInt16LE(1, 22);
  header.writeUInt32LE(48_000, 24);
  header.writeUInt32LE(96_000, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36);
  header.writeUInt32LE(data.length, 40);
  return Buffer.concat([header, data]);
}

async function expectRegions(window: Page, regions: number): Promise<void> {
  await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
    'aria-label',
    new RegExp(`^Tracks area: ${regions} regions? on `)
  );
}

// The region clipboard through the real app's keys (#88): Playwright's key presses reach the page without passing the
// native menu, whose Cut, Copy and Paste roles hold the same accelerators.
test('cuts, copies, pastes and undoes regions with the keyboard', async ({ app, window }) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-clipboard-'));
  const wav = join(scratch, 'one.wav');
  writeFileSync(wav, silence());
  try {
    await window.getByTestId('new-project-name').fill('Clipboard');
    await window.getByTestId('new-project-folder').fill(scratch);
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Clipboard');
    await window.getByTestId('add-track').click();
    await window.getByRole('group', { name: 'Track 1: Audio 1' }).click();
    await app.evaluate(({ dialog }, path) => {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
    }, wav);
    await window.getByRole('button', { name: 'Import audio' }).click();
    await expectRegions(window, 1);

    // The region lies at the playhead, on the first lane: past the ruler and into it.
    const timeline = window.locator('an-timeline');
    await timeline.click({ position: { x: 40, y: 60 } });
    await window.keyboard.press('ControlOrMeta+c');
    await window.keyboard.press('ControlOrMeta+v');
    await expectRegions(window, 2);
    await window.keyboard.press('ControlOrMeta+x');
    await expectRegions(window, 1);
    await window.keyboard.press('ControlOrMeta+z');
    await expectRegions(window, 2);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
