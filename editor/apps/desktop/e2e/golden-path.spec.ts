import type { Page } from '@playwright/test';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

/** A 2-second 440 Hz mono WAV at 48 kHz, 16-bit: what the project imports. */
function writeToneWav(path: string, seconds = 2, sampleRate = 48_000): void {
  const frames = seconds * sampleRate;
  const data = Buffer.alloc(frames * 2);
  for (let i = 0; i < frames; i++) {
    data.writeInt16LE(Math.round(Math.sin((2 * Math.PI * 440 * i) / sampleRate) * 12_000), 2 * i);
  }
  const header = Buffer.alloc(44);
  header.write('RIFF', 0, 'ascii');
  header.writeUInt32LE(36 + data.length, 4);
  header.write('WAVE', 8, 'ascii');
  header.write('fmt ', 12, 'ascii');
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20); // PCM
  header.writeUInt16LE(1, 22); // mono
  header.writeUInt32LE(sampleRate, 24);
  header.writeUInt32LE(sampleRate * 2, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36, 'ascii');
  header.writeUInt32LE(data.length, 40);
  writeFileSync(path, Buffer.concat([header, data]));
}

/** The Tracks area's canvas says how many regions and tracks it draws. */
async function expectRegions(window: Page, regions: number, tracks: number): Promise<void> {
  await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
    'aria-label',
    `Tracks area: ${regions} ${regions === 1 ? 'region' : 'regions'} on ${tracks} ${tracks === 1 ? 'track' : 'tracks'}`
  );
}

// Lane geometry at the default view: the ruler is 24 px, lanes 72 px, 48 px per quarter note (120 bpm: 96 px a second).
const Ruler = 24;
const Lane = 72;
const laneY = (lane: number) => Ruler + lane * Lane + Lane * 0.75; // the lower half: move, not fade

// The golden path of docs/02-phase-1.md against the real app: Electron, the built editor, anode-server and anode-host on
// the null device. It needs workstreams A and B (the Server's endpoints are stubs until then), so it is marked fixme
// until integration; it compiles and runs with `nx e2e desktop` and should pass once they land.
test.fixme('creates, imports, records, edits, undoes, saves, reopens and plays a project', async ({ app, window }) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-golden-'));
  const wav = join(scratch, 'tone.wav');
  writeToneWav(wav);
  try {
    // Create a project in the scratch folder.
    await expect(window.getByRole('region', { name: 'Projects' })).toBeVisible();
    await window.getByTestId('new-project-name').fill('Golden');
    await window.getByTestId('new-project-folder').fill(scratch);
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Golden');

    // Two tracks.
    await window.getByTestId('add-track').click();
    await window.getByTestId('add-track').click();
    await expectRegions(window, 0, 2);

    // Import the WAV onto track 1 at the start, through the native Import dialog (answered here) and ImportAudioFile.
    await window.getByRole('group', { name: 'Track 1: Audio 1' }).click();
    await app.evaluate(({ dialog }, path) => {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
    }, wav);
    await window.getByRole('button', { name: 'Import audio' }).click();
    await expectRegions(window, 1, 2);

    // Record two seconds on track 2 with the null device.
    await window.getByRole('button', { name: 'Record enable Audio 2' }).click();
    await window.getByTestId('record').click();
    await window.waitForTimeout(2_000);
    await window.getByRole('button', { name: 'Stop' }).click();
    await expectRegions(window, 2, 2);

    // Split the imported region at beat 3 (96 px) with the scissors, then move its second half to track 2's bar 3.
    const timeline = window.locator('an-timeline');
    await window.getByRole('radio', { name: 'Scissors' }).click();
    await timeline.click({ position: { x: 96, y: laneY(0) } });
    await expectRegions(window, 3, 2);
    await window.getByRole('radio', { name: 'Pointer' }).click();
    await timeline.dragTo(timeline, {
      sourcePosition: { x: 140, y: laneY(0) },
      targetPosition: { x: 140 + 192 * 2, y: laneY(1) }
    });
    await expect(window.getByRole('button', { name: 'Undo' })).toBeEnabled();

    // Undo the move and the split, redo the split.
    await window.keyboard.press('ControlOrMeta+z');
    await window.keyboard.press('ControlOrMeta+z');
    await expectRegions(window, 2, 2);
    await window.keyboard.press('ControlOrMeta+Shift+z');
    await expectRegions(window, 3, 2);

    // Save, close and reopen from the recent list.
    await expect(window.getByTestId('unsaved')).toBeVisible();
    await window.keyboard.press('ControlOrMeta+s');
    await expect(window.getByTestId('unsaved')).toBeHidden();
    await window.getByRole('button', { name: 'Close project' }).click();
    await expect(window.getByRole('region', { name: 'Projects' })).toBeVisible();
    await window.getByTestId('recent-Golden').click();
    await expectRegions(window, 3, 2);

    // Play: the LCD moves.
    await window.getByTestId('play').click();
    await expect(window.getByTestId('lcd-bars')).not.toHaveText('1 1 1   1', { timeout: 5_000 });
    await window.getByRole('button', { name: 'Stop' }).click();
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
