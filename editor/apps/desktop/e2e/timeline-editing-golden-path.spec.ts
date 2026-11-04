import type { ElectronApplication, Page } from '@playwright/test';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

/** Four clicks a half second apart (a short burst, then silence) as a two-second mono 16-bit WAV at 48 kHz. */
function clicks(): Buffer {
  const frames = 48_000 * 2;
  const data = Buffer.alloc(frames * 2);
  for (let beat = 0; beat < 4; beat++) {
    const at = beat * 24_000;
    for (let i = 0; i < 480; i++) {
      data.writeInt16LE(i % 2 === 0 ? 24_000 : -24_000, (at + i) * 2);
    }
  }
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

/** A new project with one audio track holding a two-second region at the start, the region selected. */
async function projectWithRegion(app: ElectronApplication, window: Page, scratch: string): Promise<void> {
  const wav = join(scratch, 'clicks.wav');
  writeFileSync(wav, clicks());
  await window.getByTestId('new-project-name').fill('Timeline');
  await window.getByTestId('new-project-folder').fill(scratch);
  await window.getByTestId('create-project').click();
  await expect(window.getByTestId('project-name')).toHaveText('Timeline');
  await window.getByTestId('add-track').click();
  await window.getByRole('group', { name: 'Track 1: Audio 1' }).click();
  await app.evaluate(({ dialog }, path) => {
    dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
  }, wav);
  await window.getByRole('button', { name: 'Import audio' }).click();
  await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
    'aria-label',
    /^Tracks area: 1 region on /
  );
  await window.locator('an-timeline').click({ position: { x: 40, y: 60 } });
}

/** Asserts the playhead's bar, beat, division and tick on the LCD. */
async function expectPlayhead(window: Page, bars: RegExp): Promise<void> {
  await expect(window.getByTestId('lcd-bars')).toHaveText(bars);
}

/** Presses `key` until the playhead shows `bars`. */
async function pressUntil(window: Page, key: string, bars: RegExp): Promise<void> {
  await expect(async () => {
    await window.keyboard.press(key);
    await expect(window.getByTestId('lcd-bars')).toHaveText(bars, { timeout: 500 });
  }).toPass();
}

// Two seconds at 120 bpm is exactly one 4/4 bar: the region ends at bar 2.
test('nudges regions and moves the playhead by region edge and transient from the keyboard', async ({
  app,
  window
}) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-timeline-'));
  try {
    await projectWithRegion(app, window, scratch);

    await window.keyboard.press('Control+Alt+ArrowRight');
    await expectPlayhead(window, /^\s*2\s+1\s+1\s+1/);
    await window.keyboard.press('Control+Alt+ArrowLeft');
    await expectPlayhead(window, /^\s*1\s+1\s+1\s+1/);

    await window.keyboard.press('Alt+Shift+ArrowRight');
    await expectPlayhead(window, /^\s*1\s+2\s+1\s+1/);

    await window.getByTestId('nudge-value').click();
    await window.getByRole('menuitemradio', { name: 'Bar' }).click();
    await window.locator('an-timeline').click({ position: { x: 40, y: 60 } });
    await window.keyboard.press('Alt+ArrowRight');
    // The edit reaches the editor as a delta: the keys go on until the playhead finds the moved region's end.
    await pressUntil(window, 'Control+Alt+ArrowRight', /^\s*3\s+1\s+1\s+1/);

    await window.keyboard.press('ControlOrMeta+z');
    await pressUntil(window, 'Control+Alt+ArrowLeft', /^\s*1\s+1\s+1\s+1/);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

// A loop by the inspector, and by dragging the region's corner: the next region edge is where its repeats end.
test('loops a region from the inspector and by its corner', async ({ app, window }) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-timeline-'));
  try {
    await projectWithRegion(app, window, scratch);

    await window.getByTestId('region-repeats').locator('input').fill('2');
    await window.getByTestId('region-repeats').locator('input').press('Enter');
    await window.locator('an-timeline').click({ position: { x: 40, y: 60 } });
    await window.keyboard.press('Control+Alt+ArrowRight');
    await expectPlayhead(window, /^\s*2\s+1\s+1\s+1/);
    await window.keyboard.press('Control+Alt+ArrowRight');
    await expectPlayhead(window, /^\s*4\s+1\s+1\s+1/);

    await window.keyboard.press('ControlOrMeta+z');
    await window.keyboard.press('Control+Alt+ArrowLeft');
    await expectPlayhead(window, /^\s*2\s+1\s+1\s+1/);
    await window.keyboard.press('Control+Alt+ArrowLeft');
    await expectPlayhead(window, /^\s*1\s+1\s+1\s+1/);

    // One bar is 192 px at the default zoom: the corner is at the top of the first lane's right end.
    const timeline = window.locator('an-timeline');
    const ruler = 24;
    await timeline.dragTo(timeline, {
      sourcePosition: { x: 191, y: ruler + 3 },
      targetPosition: { x: 3 * 192, y: ruler + 3 }
    });
    await expect(window.getByTestId('region-repeats').locator('input')).toHaveValue('2');
    await window.keyboard.press('Control+Alt+ArrowRight');
    await window.keyboard.press('Control+Alt+ArrowRight');
    await expectPlayhead(window, /^\s*4\s+1\s+1\s+1/);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

// ⌥-drag of a region's lower right edge: twice as long, its file frames untouched; the inspector shows it, undo undoes it.
test('time-stretches a region by dragging its edge with Option', async ({ app, window }) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-timeline-'));
  try {
    await projectWithRegion(app, window, scratch);
    const box = await window.locator('an-timeline').boundingBox();
    if (!box) {
      throw new Error('no timeline');
    }

    // One bar is 192 px at the default zoom; the lane starts under the 24 px ruler.
    const y = box.y + 24 + 54;
    await window.mouse.move(box.x + 191, y);
    await window.keyboard.down('Alt');
    await window.mouse.down();
    await window.mouse.move(box.x + 300, y, { steps: 4 });
    await window.mouse.move(box.x + 2 * 192, y, { steps: 4 });
    await window.mouse.up();
    await window.keyboard.up('Alt');

    await expect(window.getByTestId('region-stretch').locator('input')).toHaveValue(/^200/);
    await window.keyboard.press('Control+Alt+ArrowRight');
    await expectPlayhead(window, /^\s*3\s+1\s+1\s+1/);

    await window.keyboard.press('ControlOrMeta+z');
    await expect(window.getByTestId('region-stretch').locator('input')).toHaveValue(/^100/);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
