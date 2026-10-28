import type { Page } from '@playwright/test';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

/** A mono 16-bit WAV of a 440 Hz tone at 48 kHz. */
function writeToneWav(path: string, seconds = 4, sampleRate = 48_000): void {
  const frames = seconds * sampleRate;
  const data = Buffer.alloc(frames * 2);
  for (let i = 0; i < frames; i++) {
    data.writeInt16LE(Math.round(Math.sin((2 * Math.PI * 440 * i) / sampleRate) * 12_000), 2 * i);
  }
  const header = Buffer.alloc(44);
  header.write('RIFF', 0, 'ascii');
  header.writeUInt32LE(36 + data.length, 4);
  header.write('WAVEfmt ', 8, 'ascii');
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20);
  header.writeUInt16LE(1, 22);
  header.writeUInt32LE(sampleRate, 24);
  header.writeUInt32LE(sampleRate * 2, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36, 'ascii');
  header.writeUInt32LE(data.length, 40);
  writeFileSync(path, Buffer.concat([header, data]));
}

/** A type-0 Standard MIDI File: one track, 120 bpm, four quarter notes. */
function writeMidiFile(path: string): void {
  const events = [
    [0x00, 0xff, 0x51, 0x03, 0x07, 0xa1, 0x20], // tempo 500000 µs
    [0x00, 0xff, 0x58, 0x04, 0x04, 0x02, 0x18, 0x08], // 4/4
    ...[60, 62, 64, 65].flatMap(pitch => [
      [0x00, 0x90, pitch, 0x60],
      [0x83, 0x60, 0x80, pitch, 0x00] // 480 ticks later
    ]),
    [0x00, 0xff, 0x2f, 0x00]
  ].flat();
  const track = Buffer.concat([Buffer.from('MTrk'), Buffer.alloc(4), Buffer.from(events)]);
  track.writeUInt32BE(events.length, 4);
  const header = Buffer.from([0x4d, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xe0]);
  writeFileSync(path, Buffer.concat([header, track]));
}

/** The Tracks area's canvas says how many regions (take folders included) and tracks it draws. */
async function expectRegions(window: Page, regions: number, tracks: number): Promise<void> {
  await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
    'aria-label',
    `Tracks area: ${regions} ${regions === 1 ? 'region' : 'regions'} on ${tracks} ${tracks === 1 ? 'track' : 'tracks'}`
  );
}

// Lane geometry at the default view: the ruler is 24 px, lanes 72 px, 48 px per quarter note (120 bpm), a bar 192 px.
const Ruler = 24;
const Lane = 72;
const Bar = 192;

// The Phase 3 exit criterion (docs/04-phase-3.md) against the real app on the null device. It needs workstreams A
// (cycle recording, punch, flashback on the host) and B (the Server's Phase 3 commands and endpoints), so it is
// fixme until the integration; it compiles and its steps drive the editor's Phase 3 UI by role and test id.
test.fixme('tracks and edits a session: takes, comping, punch, flashback, tempo, arrangement, crossfades, stacks, screensets, key commands, import, alternatives and backups', async ({
  app,
  window
}) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-arranging-golden-'));
  const wav = join(scratch, 'tone.wav');
  const mid = join(scratch, 'song.mid');
  writeToneWav(wav);
  writeMidiFile(mid);
  const timeline = window.locator('an-timeline');
  const laneY = (lane: number, global = 0) => Ruler + global + lane * Lane + Lane * 0.75;
  try {
    await window.getByTestId('new-project-name').fill('Arranging');
    await window.getByTestId('new-project-folder').fill(scratch);
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Arranging');
    await window.getByTestId('add-track').click();
    await window.getByTestId('add-track').click();
    await expectRegions(window, 0, 2);

    // Cycle-record three takes into a take folder: bars 1–2 cycled three times on track 1.
    await window.getByRole('button', { name: 'Record enable Audio 1' }).click();
    await window.getByTestId('cycle-mode').click();
    await window.getByRole('menuitemradio', { name: 'Create Take Folders' }).click();
    await window.getByRole('button', { name: 'Cycle', exact: true }).click();
    await window.getByTestId('record').click();
    await window.waitForTimeout(3 * 4_000 + 500); // three passes of two bars at 120 bpm, into a fourth
    await window.getByRole('button', { name: 'Stop' }).click();
    await expectRegions(window, 1, 2);
    await window.getByRole('button', { name: 'Cycle', exact: true }).click();

    // Open the folder and comp it with Quick Swipe: the first bar from take 1.
    await timeline.dblclick({ position: { x: Bar / 2, y: laneY(0) } });
    await expect(window.getByRole('group', { name: 'Take: Take 1' })).toBeVisible();
    await timeline.dragTo(timeline, {
      sourcePosition: { x: 10, y: laneY(1) },
      targetPosition: { x: Bar, y: laneY(1) }
    });
    await expect(window.getByRole('img', { name: 'Partly in the comp' }).first()).toBeVisible();
    await timeline.dblclick({ position: { x: Bar / 2, y: laneY(0) } }); // close it again
    await window.getByRole('button', { name: 'Record enable Audio 1' }).click();

    // Punch in with autopunch on track 2: punch bar 2 only, record from bar 1 to bar 3.
    await window.getByRole('button', { name: 'Record enable Audio 2' }).click();
    await window.getByTestId('autopunch').click();
    await expect(window.getByTestId('autopunch')).toHaveAttribute('aria-pressed', 'true');
    await timeline.dragTo(timeline, {
      sourcePosition: { x: Bar, y: Ruler - 2 },
      targetPosition: { x: 2 * Bar, y: Ruler - 2 }
    });
    await window.getByRole('button', { name: 'Go to start' }).click();
    await window.getByTestId('record').click();
    await window.waitForTimeout(6_000);
    await window.getByRole('button', { name: 'Stop' }).click();
    await expectRegions(window, 2, 2);
    await window.getByTestId('autopunch').click();

    // Flashback Capture: play two seconds, stop, ⇧R.
    await window.getByRole('button', { name: 'Go to start' }).click();
    await window.getByTestId('play').click();
    await window.waitForTimeout(2_000);
    await window.getByRole('button', { name: 'Stop' }).click();
    await window.keyboard.press('Shift+R');
    await expectRegions(window, 3, 2);
    await window.getByRole('button', { name: 'Record enable Audio 2' }).click();

    // A tempo ramp and a signature change in the global tracks.
    await window.getByTestId('global-tracks').click();
    const tempo = window.getByTestId('global-tempo');
    await tempo.dblclick({ position: { x: 4 * Bar, y: 10 } });
    await window.getByTestId('tempo-point-0').click({ modifiers: ['Alt'] }); // ramp from the start to the new point
    await expect(window.getByRole('slider', { name: 'Tempo point 1, ramp' })).toBeVisible();
    await window.getByTestId('global-signature').dblclick({ position: { x: 2 * Bar + 4, y: 10 } });
    await window.getByRole('textbox', { name: 'Time signature' }).fill('3/4');
    await window.keyboard.press('Enter');
    await expect(window.getByRole('button', { name: 'Signature 3/4' })).toBeVisible();

    // Lay out arrangement sections and move one: Intro, Verse, then drag Verse before Intro.
    await window.getByTestId('add-section').click();
    await window.getByTestId('add-section').click();
    await expect(window.getByTestId('section-Verse')).toBeVisible();
    const verse = await window.getByTestId('section-Verse').boundingBox();
    const intro = await window.getByTestId('section-Intro').boundingBox();
    if (verse && intro) {
      await window.mouse.move(verse.x + 10, verse.y + 5);
      await window.mouse.down();
      await window.mouse.move(intro.x + 2, intro.y + 5, { steps: 8 });
      await window.mouse.up();
    }
    await expect(window.getByRole('button', { name: 'Undo' })).toBeEnabled();
    await window.getByTestId('global-tracks').click();

    // Crossfade two regions: import the tone twice on track 2 back to back, then the Fade tool across the boundary.
    await window.getByRole('group', { name: 'Track 2: Audio 2' }).click();
    await app.evaluate(({ dialog }, path) => {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path, path] });
    }, wav);
    await window.getByRole('button', { name: 'Import audio' }).click();
    await window.getByRole('radio', { name: 'Fade' }).click();
    await timeline.dragTo(timeline, {
      sourcePosition: { x: 2 * Bar, y: laneY(1) },
      targetPosition: { x: 2 * Bar + 20, y: laneY(1) }
    });
    await window.getByRole('radio', { name: 'Pointer' }).click();

    // Stack tracks in a folder: select both headers, ⇧⌘D, collapse.
    await window.getByRole('group', { name: 'Track 1: Audio 1' }).click();
    await window.getByRole('group', { name: 'Track 2: Audio 2' }).click({ modifiers: ['Shift'] });
    await window.keyboard.press('ControlOrMeta+Shift+d');
    await expect(window.getByTestId('stack-disclosure')).toBeVisible();
    await window.getByTestId('stack-disclosure').click();
    await expect(window.getByRole('group', { name: 'Track 2: Audio 1' })).toBeHidden();
    await window.getByTestId('stack-disclosure').click();

    // Switch screensets: 2 is a fresh layout, 1 comes back.
    await window.locator('body').click({ position: { x: 5, y: 300 } });
    await window.keyboard.press('2');
    await expect(window.getByTestId('screenset-number')).toHaveText('2');
    await window.keyboard.press('1');
    await expect(window.getByTestId('screenset-number')).toHaveText('1');

    // Rebind a key command: Cycle Mode to L, then L toggles the cycle.
    await window.keyboard.press('Alt+k');
    await window.getByTestId('key-search').fill('cycle mode');
    await window.getByRole('button', { name: 'Learn a key for Cycle Mode' }).click();
    await window.keyboard.press('l');
    await expect(window.getByTestId('key-transport.cycle')).toContainText('L');
    await window.getByRole('button', { name: 'Done' }).click();
    await window.keyboard.press('l');
    await expect(window.getByRole('button', { name: 'Cycle', exact: true })).toHaveAttribute('aria-pressed', 'true');

    // Import from the All Files browser: the MIDI file (double-click imports it).
    await window.keyboard.press('f');
    await expect(window.getByTestId('all-files')).toBeVisible();
    for (const part of scratch.split('/').filter(Boolean)) {
      const open = window.getByRole('button', { name: `Open ${part}` });
      if (await open.isVisible().catch(() => false)) {
        await open.click();
      }
    }
    await window.getByTestId('file-song.mid').dblclick();
    await expect(window.getByRole('group', { name: /^Track \d+: song/ }).first()).toBeVisible();

    // Save an alternative and revert to a backup.
    await window.keyboard.press('ControlOrMeta+s');
    await window.getByTestId('project-menu').click();
    await window.getByTestId('new-alternative').click();
    await window.getByTestId('alternative-name').fill('Remix');
    await window.getByTestId('save-alternative').click();
    await window.getByTestId('add-track').click();
    await window.keyboard.press('ControlOrMeta+s');
    await window.getByTestId('project-menu').click();
    await window.getByTestId('backups').click();
    await window.getByTestId('backup-0').click();
    await expect(window.getByTestId('project-name')).toBeVisible();
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
