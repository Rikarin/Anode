import type { Page } from '@playwright/test';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

/** The Tracks area's canvas says how many regions and tracks it draws. */
async function expectRegions(window: Page, regions: number, tracks: number): Promise<void> {
  await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
    'aria-label',
    `Tracks area: ${regions} ${regions === 1 ? 'region' : 'regions'} on ${tracks} ${tracks === 1 ? 'track' : 'tracks'}`
  );
}

/** The Piano Roll's canvas says how many notes it shows. */
async function expectNotes(window: Page, notes: number): Promise<void> {
  await expect(window.getByRole('img', { name: /^Piano Roll:/ })).toHaveAttribute(
    'aria-label',
    new RegExp(`^Piano Roll: ${notes} ${notes === 1 ? 'note' : 'notes'} in`)
  );
}

/** A one-region SFZ over a 0.5 s sine WAV, for the sampler. */
function writeSfz(folder: string): string {
  const rate = 48_000;
  const frames = rate / 2;
  const data = Buffer.alloc(frames * 2);
  for (let i = 0; i < frames; i++) {
    data.writeInt16LE(Math.round(Math.sin((2 * Math.PI * 261.63 * i) / rate) * 12_000), 2 * i);
  }
  const header = Buffer.alloc(44);
  header.write('RIFF', 0, 'ascii');
  header.writeUInt32LE(36 + data.length, 4);
  header.write('WAVEfmt ', 8, 'ascii');
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20);
  header.writeUInt16LE(1, 22);
  header.writeUInt32LE(rate, 24);
  header.writeUInt32LE(rate * 2, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36, 'ascii');
  header.writeUInt32LE(data.length, 40);
  writeFileSync(join(folder, 'c3.wav'), Buffer.concat([header, data]));
  const sfz = join(folder, 'tone.sfz');
  writeFileSync(sfz, '<region> sample=c3.wav pitch_keycenter=60 lokey=0 hikey=127\n');
  return sfz;
}

// Lane geometry at the default view: the ruler is 24 px, lanes 72 px, 48 px per quarter note.
const Ruler = 24;
const Lane = 72;
const laneY = (lane: number) => Ruler + lane * Lane + Lane / 2;

// The Phase 2 golden path (docs/03-phase-2.md, exit criterion) against the real app on the null device.
test('makes a song with MIDI and audio: synth, sampler, CLAP, external MIDI, Piano Roll, save and reopen', async ({
  app,
  window
}) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-midi-golden-'));
  const sfz = writeSfz(scratch);
  try {
    await window.getByTestId('new-project-name').fill('Midi Golden');
    await window.getByTestId('new-project-folder').fill(scratch);
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Midi Golden');

    // An audio track and an instrument track (the built-in synth by default, selected once added).
    await window.getByTestId('add-track').click();
    await window.getByTestId('add-track-menu').click();
    await window.getByTestId('add-instrument-track').click();
    await expectRegions(window, 0, 2);
    await expect(window.getByTestId('instrument-picker')).toContainText('Anode Synth');

    // A synth parameter: the cutoff knob, by keyboard.
    const cutoff = window.getByRole('slider', { name: 'Cutoff' });
    await cutoff.focus();
    await window.keyboard.press('PageDown');
    await expect(window.getByRole('button', { name: 'Undo' })).toBeEnabled();

    // Record the on-screen keyboard (through /midi/live) on the armed instrument track.
    await window.getByRole('button', { name: 'Record enable Inst 1' }).click();
    await window.getByRole('tab', { name: 'Keyboard' }).click();
    await window.getByTestId('record').click();
    const c3 = window.getByRole('button', { name: 'C3', exact: true });
    for (let i = 0; i < 3; i++) {
      await c3.dispatchEvent('pointerdown', { button: 0, pointerId: 1 });
      await window.waitForTimeout(200);
      await c3.dispatchEvent('pointerup', { button: 0, pointerId: 1 });
      await window.waitForTimeout(200);
    }
    await expect(window.getByTestId('midi-activity').first()).toBeVisible();
    await window.getByRole('button', { name: 'Stop' }).click();
    await expectRegions(window, 1, 2);
    await window.getByRole('button', { name: 'Record enable Inst 1' }).click();

    // Edit in the Piano Roll: draw a note with the pencil, then quantise everything.
    await window.locator('an-timeline').click({ position: { x: 60, y: laneY(1) } });
    await window.getByRole('tab', { name: 'Editors' }).click();
    await expectNotes(window, 3);
    await window.getByRole('radio', { name: 'Pencil' }).last().click();
    const grid = window.locator('an-piano-roll-grid');
    const box = await grid.boundingBox();
    await grid.click({ position: { x: 150, y: (box?.height ?? 200) / 2 } });
    await expectNotes(window, 4);
    await window.getByTestId('quantize').click();
    await expectNotes(window, 4);

    // A sampler track playing an SFZ (the native dialog answered here).
    await window.getByTestId('add-track-menu').click();
    await window.getByTestId('add-instrument-track').click();
    await window.getByTestId('instrument-picker').click();
    await window.getByRole('option', { name: 'Anode Sampler' }).click();
    await app.evaluate(({ dialog }, path) => {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
    }, sfz);
    await window.getByRole('button', { name: 'Load file…' }).click();
    await expect(window.getByTestId('instrument-file')).toContainText('tone.sfz');
    await window.locator('an-timeline').dblclick({ position: { x: 30, y: laneY(2) } });
    await expectRegions(window, 2, 3);

    // A CLAP instrument (the engine's test plugin) on a third instrument track.
    await window.getByTestId('add-track-menu').click();
    await window.getByTestId('add-instrument-track').click();
    await window.getByTestId('instrument-picker').click();
    await window.getByRole('option', { name: /CLAP/ }).first().click();
    await expect(window.getByTestId('instrument-picker')).toContainText('CLAP');

    // An external MIDI track sending to the first output port.
    await window.getByTestId('add-track-menu').click();
    await window.getByTestId('add-midi-track').click();
    // A new external MIDI track sends nowhere until a port is picked.
    await expect(window.getByTestId('midi-output')).toContainText('No output');
    await window.getByTestId('midi-output').click();
    // The port list's first option (the inspector's articulation set menu has options of its own).
    await window.getByRole('listbox').getByRole('option').first().click();
    await expect(window.getByTestId('midi-output')).not.toContainText('No output');
    await window.locator('an-timeline').dblclick({ position: { x: 30, y: laneY(4) } });
    await expectRegions(window, 3, 5);

    // Play the song: audio and instruments mix.
    await window.getByTestId('play').click();
    await expect(window.getByTestId('lcd-bars')).not.toHaveText('1 1 1   1', { timeout: 5_000 });
    await window.getByRole('button', { name: 'Stop' }).click();

    // Save, close, reopen: every instrument's parameters and state come back.
    await window.keyboard.press('ControlOrMeta+s');
    await expect(window.getByTestId('unsaved')).toBeHidden();
    await window.getByRole('button', { name: 'Close project' }).click();
    await window.getByTestId('recent-Midi Golden').click();
    await expectRegions(window, 3, 5);
    await window.getByRole('group', { name: 'Track 2: Inst 1' }).click();
    await expect(window.getByRole('slider', { name: 'Cutoff' })).not.toHaveAttribute('aria-valuetext', '2.40 kHz');
    await window.getByRole('group', { name: 'Track 3: Inst 2' }).click();
    await expect(window.getByTestId('instrument-file')).toContainText('tone.sfz');
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
