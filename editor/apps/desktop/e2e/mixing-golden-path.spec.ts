import type { Locator, Page } from '@playwright/test';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

/** A mono 16-bit WAV of a 220 Hz tone at 48 kHz, at about −12 dBFS. */
function writeToneWav(path: string, seconds = 8, sampleRate = 48_000): void {
  const frames = seconds * sampleRate;
  const data = Buffer.alloc(frames * 2);
  for (let i = 0; i < frames; i++) {
    data.writeInt16LE(Math.round(Math.sin((2 * Math.PI * 220 * i) / sampleRate) * 8_200), 2 * i);
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

/**
 * The integrated loudness of a 16/24-bit PCM or 32-bit float WAV, ungated and unweighted (a mean-square estimate,
 * close enough to EBU R128 for a steady tone to check a loudness target within a dB).
 */
function roughLufs(path: string): number {
  const bytes = readFileSync(path);
  let offset = 12;
  let format = 1;
  let channels = 1;
  let bits = 16;
  let data: Buffer | null = null;
  while (offset + 8 <= bytes.length) {
    const id = bytes.toString('ascii', offset, offset + 4);
    const size = bytes.readUInt32LE(offset + 4);
    if (id === 'fmt ') {
      format = bytes.readUInt16LE(offset + 8);
      channels = bytes.readUInt16LE(offset + 10);
      bits = bytes.readUInt16LE(offset + 22);
    } else if (id === 'data') {
      data = bytes.subarray(offset + 8, offset + 8 + size);
    }
    offset += 8 + size + (size % 2);
  }
  if (!data) {
    throw new Error(`${path} has no data chunk`);
  }
  const step = bits / 8;
  let sum = 0;
  let count = 0;
  for (let i = 0; i + step <= data.length; i += step) {
    const sample =
      format === 3
        ? data.readFloatLE(i)
        : bits === 24
          ? data.readIntLE(i, 3) / 8_388_608
          : data.readInt16LE(i) / 32_768;
    sum += sample * sample;
    count++;
  }
  return -0.691 + 10 * Math.log10((sum / Math.max(1, count)) * channels);
}

/** Picks a plugin from an insert slot's menu: the category, then the plugin by name. */
async function insertPlugin(window: Page, slot: Locator, category: string, plugin: string | RegExp): Promise<void> {
  await slot.click();
  await window.getByRole('menuitem', { name: category, exact: true }).hover();
  await window.getByRole('menuitem', { name: plugin }).first().click();
}

// Lane geometry at the default view: the ruler is 24 px, lanes 72 px, 48 px per quarter note (120 bpm), a bar 192 px.
const Ruler = 24;
const Lane = 72;
const Bar = 192;

// The Phase 4 exit criterion (docs/05-phase-4.md): mix, automate, freeze, patch and bounce on the real app with the null
// device, through the mixer graph, the Server's jobs and bounce post-processing, the built-in effects and the sandboxed
// test VST3. Its steps drive the editor's Phase 4 UI by role and test id.
/** The vertical middle of an element, in its own coordinates. */
async function middleOf(locator: Locator): Promise<number> {
  const box = await locator.boundingBox();
  if (!box) {
    throw new Error('The element is not on screen.');
  }
  return box.height / 2;
}

test('mixes, automates, freezes and bounces a session: inserts, sends, routing, a group, a VCA, touch automation, freeze, patches, bounces and reopening', async ({
  app,
  window
}) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-mixing-golden-'));
  const wav = join(scratch, 'tone.wav');
  const bounces = join(scratch, 'Bounces');
  writeToneWav(wav);
  const timeline = window.locator('an-timeline');
  try {
    // 1. Record and edit (Phases 1–3): a project with an audio track holding the tone and an instrument track.
    await window.getByTestId('new-project-name').fill('Mixing');
    await window.getByTestId('new-project-folder').fill(scratch);
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Mixing');
    await window.getByTestId('add-track').click();
    await window.getByRole('group', { name: 'Track 1: Audio 1' }).click();
    await app.evaluate(({ dialog }, path) => {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
    }, wav);
    await window.getByRole('button', { name: 'Import audio' }).click();
    await window.getByTestId('add-track-menu').click();
    await window.getByTestId('add-instrument-track').click();
    await window.getByRole('group', { name: 'Track 2: Inst 1' }).click();
    await window.getByRole('radio', { name: 'Pencil' }).click();
    await timeline.click({ position: { x: Bar / 2, y: Ruler + Lane + Lane / 2 } });
    await window.getByRole('radio', { name: 'Pointer' }).click();

    // 2. Mix. Show the Mixer (X).
    await window.locator('body').click({ position: { x: 5, y: 300 } });
    await window.keyboard.press('x');
    const audio = window.getByRole('group', { name: 'Audio 1 channel strip' });
    await expect(audio).toBeVisible();

    // Inserts: Channel EQ, Compressor and the sandboxed test VST3 on Audio 1.
    await insertPlugin(window, audio.getByTestId('insert-empty'), 'EQ', /^Channel EQ/);
    await insertPlugin(window, audio.getByTestId('insert-empty'), 'Dynamics', /^Compressor/);
    await insertPlugin(window, audio.getByTestId('insert-empty'), 'Utility', /\(VST3\)$/);
    await expect(audio.getByTestId('insert-slot')).toHaveCount(3);
    // Bypass and re-enable the compressor; reorder the EQ after it with ⌥↓.
    await audio.getByRole('button', { name: /^Bypass Compressor/ }).click();
    await expect(audio.getByRole('button', { name: /^Bypass Compressor/ })).toHaveAttribute('aria-pressed', 'true');
    await audio.getByRole('button', { name: /^Bypass Compressor/ }).click();
    await audio.getByRole('button', { name: /^Insert 1 on Audio 1: Channel EQ/ }).focus();
    await window.keyboard.press('Alt+ArrowDown');
    await expect(audio.getByRole('button', { name: /^Insert 2 on Audio 1: Channel EQ/ })).toBeVisible();
    // The compressor's generic editor shows its gain reduction; turn its threshold down.
    await audio.getByRole('button', { name: /^Insert 1 on Audio 1: Compressor/ }).click();
    const editor = window.getByRole('dialog', { name: /Audio 1: Compressor/ });
    await expect(editor).toBeVisible();
    const threshold = editor.getByRole('slider', { name: 'Threshold' });
    await threshold.focus();
    await window.keyboard.press('PageDown');
    await window.keyboard.press('PageDown');
    await editor.getByRole('button', { name: /^Close/ }).click();

    // A send to a new aux with a reverb: Bus 1, the aux gets ChromaVerb-style reverb.
    await audio.getByTestId('add-send').click();
    await window.getByTestId('send-new-aux').click();
    const aux = window.getByRole('group', { name: 'Aux 1 channel strip' });
    await expect(aux).toBeVisible();
    await insertPlugin(window, aux.getByTestId('insert-empty'), 'Reverb', /^Reverb/);
    const sendLevel = audio.getByRole('slider', { name: /send level of Audio 1/ });
    await sendLevel.focus();
    await window.keyboard.press('ArrowDown');
    await expect(sendLevel).not.toHaveAttribute('aria-valuetext', '0.0 dB');

    // Output routing: the instrument to Bus 2, then a second aux for it; the aux can't route back to Bus 1 (a loop).
    const inst = window.getByRole('group', { name: 'Inst 1 channel strip' });
    await inst.getByTestId('output').click();
    await window.getByRole('menuitem', { name: 'Bus 1–16' }).hover();
    await window.locator('[role="menuitem"][data-bus="2"]').click();
    await expect(inst.getByTestId('output')).toContainText('Bus 2');
    await aux.getByTestId('output').click();
    await window.getByRole('menuitem', { name: 'Bus 1–16' }).hover();
    await expect(window.locator('[role="menuitem"][data-bus="1"]')).toBeDisabled();
    await window.keyboard.press('Escape');
    await window.keyboard.press('Escape');

    // A mixer group of the two tracks, linking volume.
    await audio.getByTestId('group-slot').click();
    await window.getByTestId('new-group').click();
    await expect(window.getByTestId('group-settings')).toBeVisible();
    await window.getByTestId('group-name').fill('Band');
    await window.getByTestId('group-name').press('Enter');
    await window.getByRole('button', { name: 'Done' }).click();
    await inst.getByTestId('group-slot').click();
    await window.getByRole('menuitemradio', { name: '1: Band' }).click();
    await expect(inst.getByTestId('group-slot')).toContainText('Band');

    // A VCA controlling both.
    await audio.getByTestId('vca-slot').click();
    await window.getByRole('menuitem', { name: 'New VCA' }).click();
    const vca = window.getByRole('group', { name: 'VCA 1 channel strip' });
    await expect(vca).toBeVisible();
    await inst.getByTestId('vca-slot').click();
    await window.getByRole('menuitemradio', { name: 'VCA 1' }).click();
    await vca.getByRole('slider', { name: 'VCA 1 volume' }).focus();
    await window.keyboard.press('ArrowDown');

    // 3. Automate: volume drawn in the Tracks area, the compressor's threshold, and a touch-recorded fader pass.
    await window.keyboard.press('a');
    await expect(window.getByTestId('automation-mode').first()).toBeVisible();
    // Audio 1's volume lane (under its track; the lane takes the clicks over the timeline): two points.
    const volumeLane = window.getByRole('group', { name: 'Audio 1 Volume automation' });
    const volumeMiddle = await middleOf(volumeLane);
    await volumeLane.click({ position: { x: Bar, y: volumeMiddle } });
    await volumeLane.click({ position: { x: 3 * Bar, y: volumeMiddle - 20 } });
    await expect(window.getByRole('slider', { name: /Volume automation point 2/ })).toBeVisible();
    // A lane for the compressor's threshold.
    await window.getByTestId('add-automation-lane').first().click();
    // The lane just added to Audio 1 shows the next target it did not show (pan); switch it to the threshold.
    await window.getByRole('button', { name: 'Automation parameter of Audio 1: Pan' }).click();
    await window
      .getByRole('menuitemradio', { name: /Threshold/ })
      .first()
      .click();
    const thresholdLane = window.getByRole('group', { name: 'Audio 1 Threshold automation' });
    await thresholdLane.click({ position: { x: 2 * Bar, y: await middleOf(thresholdLane) } });
    // Touch mode, then move the fader while playing.
    await window.getByTestId('automation-mode').first().click();
    await window.getByRole('menuitemradio', { name: 'Touch' }).click();
    await window.getByTestId('play').click();
    const fader = audio.getByRole('slider', { name: 'Audio 1 volume' });
    await fader.focus();
    for (let i = 0; i < 6; i++) {
      await window.keyboard.press('ArrowUp');
      await window.waitForTimeout(150);
    }
    await window.getByRole('button', { name: 'Stop' }).click();
    await expect(window.getByRole('slider', { name: /Volume automation point/ })).not.toHaveCount(2);
    await window.keyboard.press('a');

    // 4. Freeze the instrument track.
    await window.getByRole('group', { name: 'Track 2: Inst 1' }).getByTestId('freeze-track').click();
    await expect(window.getByRole('group', { name: 'Track 2: Inst 1' }).getByTestId('freeze-track')).toHaveAttribute(
      'aria-pressed',
      'true',
      { timeout: 30_000 }
    );

    // 5. Save Audio 1's strip as a patch, apply it to a new audio track.
    await window.getByRole('group', { name: 'Track 1: Audio 1' }).click();
    // The Library follows the selection: wait for it to offer Audio 1's strip before naming the patch.
    await expect(window.getByText('Save Audio 1 as a patch')).toBeVisible();
    await window.getByTestId('patch-name').fill('Tone Chain');
    await window.getByTestId('patch-category').fill('Golden');
    await window.getByTestId('save-patch').click();
    await window.getByTestId('add-track').click();
    await window.getByRole('group', { name: /^Track \d+: Audio 2$/ }).click();
    await window.getByRole('button', { name: 'Apply patch Tone Chain to Audio 2' }).click();
    await expect(window.getByRole('group', { name: 'Audio 2 channel strip' }).getByTestId('insert-slot')).toHaveCount(
      3
    );

    // 6. Bounce the mix to WAV with loudness normalisation, then stems to AIFF with dither; check the target.
    await window.keyboard.press('ControlOrMeta+b');
    await expect(window.getByTestId('bounce-dialog')).toBeVisible();
    await window.getByTestId('bounce-name').fill('Mix');
    await window.getByTestId('bounce-folder').fill(bounces);
    await window.getByTestId('bounce-end').locator('input').fill('3');
    await window.getByTestId('bounce-normalize').click();
    await window.getByRole('option', { name: 'Loudness (EBU R128)' }).click();
    await window.getByTestId('bounce-lufs').locator('input').fill('-16');
    await window.getByTestId('bounce-start-button').click();
    await expect(window.getByTestId('bounce-dialog')).toBeHidden({ timeout: 60_000 });
    await expect(window.getByText('Bounced Mix.wav')).toBeVisible();
    expect(existsSync(join(bounces, 'Mix.wav'))).toBe(true);
    expect(Math.abs(roughLufs(join(bounces, 'Mix.wav')) - -16)).toBeLessThan(1.5);

    await window.keyboard.press('ControlOrMeta+b');
    await window.getByTestId('bounce-name').fill('Stems');
    await window.getByTestId('bounce-folder').fill(bounces);
    await window.getByTestId('bounce-end').locator('input').fill('3');
    await window.getByTestId('bounce-source').getByRole('radio', { name: 'Stems' }).click();
    await window.getByTestId('bounce-format').getByRole('radio', { name: 'AIFF' }).click();
    await window.getByTestId('bounce-bit-depth').click();
    await window.getByRole('option', { name: '16-bit' }).click();
    await window.getByTestId('bounce-dither').click();
    await window.getByRole('option', { name: 'Noise Shaped' }).click();
    await window.getByTestId('bounce-start-button').click();
    await expect(window.getByTestId('bounce-dialog')).toBeHidden({ timeout: 60_000 });
    expect(existsSync(join(bounces, 'Stems - Audio 1.aif'))).toBe(true);

    // 7. Save, close and reopen: everything is intact.
    await window.keyboard.press('ControlOrMeta+s');
    await expect(window.getByTestId('unsaved')).toHaveCount(0);
    await window.getByRole('button', { name: 'Close project' }).click();
    await window.getByTestId('recent-Mixing').click();
    await expect(window.getByTestId('project-name')).toHaveText('Mixing');
    await window.keyboard.press('x');
    const reopened = window.getByRole('group', { name: 'Audio 1 channel strip' });
    await expect(reopened.getByTestId('insert-slot')).toHaveCount(3);
    await expect(reopened.getByTestId('group-slot')).toContainText('Band');
    await expect(window.getByRole('group', { name: 'Aux 1 channel strip' })).toBeVisible();
    await expect(window.getByRole('group', { name: 'VCA 1 channel strip' })).toBeVisible();
    await expect(
      window.getByRole('group', { name: /^Track \d+: Inst 1$/ }).getByTestId('freeze-track')
    ).toHaveAttribute('aria-pressed', 'true');
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
