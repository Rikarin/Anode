import type { Locator, Page } from '@playwright/test';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

/** A mono 16-bit WAV of a tone at 48 kHz, at about −6 dBFS. */
function writeToneWav(path: string, hz = 1_000, seconds = 30, sampleRate = 48_000): void {
  const frames = seconds * sampleRate;
  const data = Buffer.alloc(frames * 2);
  for (let i = 0; i < frames; i++) {
    data.writeInt16LE(Math.round(Math.sin((2 * Math.PI * hz * i) / sampleRate) * 16_000), 2 * i);
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

/** Adds an insert from its category's menu to a strip's first empty slot. */
async function addInsert(window: Page, strip: Locator, category: string, plugin: RegExp): Promise<void> {
  await strip.getByTestId('insert-empty').first().click();
  await window.getByRole('menuitem', { name: category, exact: true }).hover();
  await window.getByRole('menuitem', { name: plugin }).first().click();
}

/** Opens the editor of the strip's insert `name`. */
async function openEditor(window: Page, strip: Locator, name: RegExp, title: RegExp): Promise<Locator> {
  await strip.getByRole('button', { name }).click();
  const editor = window.getByRole('dialog', { name: title });
  await expect(editor).toBeVisible();
  return editor;
}

/** How high the analyser's peak rises, 0…1 of the graph, from its path. */
async function analyserTop(editor: Locator): Promise<number> {
  const path = (await editor.getByTestId('eq-analyser').getAttribute('d')) ?? '';
  const heights = [...path.matchAll(/L[\d.]+,([\d.]+)/g)].map(m => Number(m[1]));
  return 1 - Math.min(...heights) / 240;
}

// The live displays through the real engine, with a tone playing on the null device: the numbers on screen come from the
// DSP, over IPC and the meters event, only while a window shows them.
test('shows live plugin displays from the real engine', async ({ app, window }) => {
  test.setTimeout(360_000); // two 30 s imports, each with its tempo analysis, on a loaded machine
  const scratch = mkdtempSync(join(tmpdir(), 'anode-effects-golden-'));
  const wav = join(scratch, 'tone.wav');
  const low = join(scratch, 'low.wav');
  writeToneWav(wav);
  writeToneWav(low, 330);
  try {
    await window.getByTestId('new-project-name').fill('Effects');
    await window.getByTestId('new-project-folder').fill(scratch);
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Effects');
    await window.getByTestId('add-track').click();
    await window.getByRole('group', { name: 'Track 1: Audio 1' }).click();
    await app.evaluate(({ dialog }, path) => {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
    }, wav);
    await window.getByRole('button', { name: 'Import audio' }).click();
    await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
      'aria-label',
      'Tracks area: 1 region on 1 track',
      { timeout: 120_000 } // the import analyses the file's tempo first, which takes a while on a loaded machine
    );
    // A second track, a 330 Hz tone: what the first one's Side Chain effects take.
    await window.getByTestId('add-track').click();
    await window.getByRole('group', { name: 'Track 2: Audio 2' }).click();
    await app.evaluate(({ dialog }, path) => {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
    }, low);
    await window.getByRole('button', { name: 'Import audio' }).click();
    await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
      'aria-label',
      'Tracks area: 2 regions on 2 tracks',
      { timeout: 120_000 }
    );
    await window.locator('body').click({ position: { x: 5, y: 300 } });
    await window.keyboard.press('x');
    const strip = window.getByRole('group', { name: 'Audio 1 channel strip' });
    await expect(strip).toBeVisible();
    const second = window.getByRole('group', { name: 'Audio 2 channel strip' });

    // Channel EQ: the curve, a handle dragged, the analyser live while the tone plays.
    await addInsert(window, strip, 'EQ', /^Channel EQ/);
    const eq = await openEditor(window, strip, /^Insert 1 on Audio 1: Channel EQ/, /Audio 1: Channel EQ/);
    await expect(eq.getByTestId('eq-panel')).toBeVisible();
    await expect(eq.getByTestId('eq-handle')).toHaveCount(8);
    // The level reading lands a moment after the window opens and moves the panel down.
    await expect(eq.getByRole('meter', { name: 'Level' })).toBeVisible();
    const peak = eq.locator('[data-band="peak3"]');
    await expect(peak).toHaveAttribute('aria-valuetext', /^2\.00 kHz, \+0\.0 dB/);
    const box = await peak.boundingBox();
    await window.mouse.move((box?.x ?? 0) + 8, (box?.y ?? 0) + 8);
    await window.mouse.down();
    await window.mouse.move((box?.x ?? 0) - 12, (box?.y ?? 0) - 30, { steps: 4 });
    await window.mouse.up();
    await expect(peak).not.toHaveAttribute('aria-valuetext', /\+0\.0 dB/);
    const gain = eq.locator('[data-parameter="peak3Gain"] [role="slider"]');
    await expect(gain).not.toHaveAttribute('aria-valuetext', '0.0 dB');
    await window.getByRole('button', { name: 'Play', exact: true }).click();
    // The analyser's area, with the tone's peak in the band that holds 1 kHz.
    await expect
      .poll(async () => (await eq.getByTestId('eq-analyser').count()) > 0 && (await analyserTop(eq)), {
        timeout: 15_000
      })
      .toBeGreaterThan(0.6);
    // Peak 2 becomes dynamic: the tone leans on its skirt, and the band's handle shows the gain moving.
    const dynamic = eq.locator('[data-band="peak2"]');
    await dynamic.focus();
    await eq.getByTestId('eq-band-settings').locator('[data-parameter="peak2Dyn"]').locator('xui-select').click();
    await window.getByRole('listbox').getByRole('option', { name: 'On', exact: true }).click();
    await expect(dynamic).toHaveAttribute('aria-valuetext', /dynamic −[1-9]/, { timeout: 15_000 });
    await eq.getByRole('button', { name: /^Close/ }).click();

    // Multipressor: the tone is in band 3, and its meter says so.
    await addInsert(window, strip, 'Dynamics', /^Multipressor/);
    const pressor = await openEditor(window, strip, /^Insert 2 on Audio 1: Multipressor/, /Audio 1: Multipressor/);
    const band = pressor.getByTestId('band-reduction-3');
    await expect(band).toHaveAttribute('aria-valuetext', /^−[1-9]/, { timeout: 15_000 });
    await expect(pressor.getByTestId('band-reduction-1')).toHaveAttribute('aria-valuetext', /^(−0\.\d|0\.0) dB/);
    await pressor.getByRole('button', { name: /^Close/ }).click();

    // The meters, on the same tone. Each insert's editor shows what the DSP measures.
    await addInsert(window, strip, 'Metering', /^Tuner/);
    const tuner = await openEditor(window, strip, /^Insert 3 on Audio 1: Tuner/, /Audio 1: Tuner/);
    await expect(tuner.getByTestId('tuner-note')).toHaveText('B5', { timeout: 15_000 });
    await expect(tuner.getByTestId('tuner-offset')).toContainText('1000.0 Hz');
    await tuner.getByRole('button', { name: /^Close/ }).click();

    await addInsert(window, strip, 'Metering', /^Oscilloscope/);
    const scope = await openEditor(window, strip, /^Insert 4 on Audio 1: Oscilloscope/, /Audio 1: Oscilloscope/);
    // Triggered on the rising edge: the trace starts at the middle or just above it (a point is its slice's peak).
    await expect(scope.getByTestId('scope-left')).toHaveAttribute('points', /^0,(\d\d|1[01]\d)(\.\d)? /, {
      timeout: 15_000
    });
    await scope.getByRole('button', { name: /^Close/ }).click();

    await addInsert(window, strip, 'Metering', /^Phase Meter/);
    const phase = await openEditor(window, strip, /^Insert 5 on Audio 1: Phase Meter/, /Audio 1: Phase Meter/);
    await expect(phase.getByTestId('goniometer-point')).toHaveCount(256, { timeout: 15_000 });
    await expect(phase.getByRole('meter', { name: 'Correlation' }).first()).toHaveAttribute('aria-valuetext', '1.0');
    await phase.getByRole('button', { name: /^Close/ }).click();

    // The sidechain effects, on the second track, with the first as their Side Chain.
    await addInsert(window, second, 'Pitch', /^Voice Shaper/);
    await second.getByRole('button', { name: /^Insert 1 on Audio 2: Voice Shaper/ }).click({ button: 'right' });
    await window.getByRole('menuitem', { name: /^Side Chain/ }).hover();
    await window.getByRole('menuitemradio', { name: 'Audio 1' }).click();
    const shaper = await openEditor(window, second, /^Insert 1 on Audio 2: Voice Shaper/, /Audio 2: Voice Shaper/);
    await expect(shaper.getByTestId('voice-heard')).toHaveText(/^3[23]\d\.\d Hz/, { timeout: 15_000 });
    await expect(shaper.getByTestId('voice-reference')).toHaveText(/^(99\d|100\d)\.\d Hz/);
    await shaper.locator('[data-parameter="target"]').locator('xui-select').click();
    await window.getByRole('listbox').getByRole('option', { name: 'Side Chain', exact: true }).click();
    await expect(shaper.getByTestId('voice-shift')).toHaveText(/^\+19\.\d st/, { timeout: 15_000 });
    await shaper.getByRole('button', { name: /^Close/ }).click();

    await addInsert(window, second, 'Specialized', /^Vocoder/);
    await second.getByRole('button', { name: /^Insert 2 on Audio 2: Vocoder/ }).click({ button: 'right' });
    await window.getByRole('menuitem', { name: /^Side Chain/ }).hover();
    await window.getByRole('menuitemradio', { name: 'Audio 1' }).click();
    const vocoder = await openEditor(window, second, /^Insert 2 on Audio 2: Vocoder/, /Audio 2: Vocoder/);
    await expect(vocoder.getByTestId('vocoder-band')).toHaveCount(20, { timeout: 15_000 });
    await vocoder.locator('[data-parameter="source"]').locator('xui-select').click();
    await window.getByRole('listbox').getByRole('option', { name: 'Side Chain Voice', exact: true }).click();
    // Connected, so the panel asks for nothing.
    await expect(vocoder.getByTestId('vocoder-hint')).toHaveCount(0);
    await vocoder.getByRole('button', { name: /^Close/ }).click();
    await addInsert(window, strip, 'Dynamics', /^Expander/);
    const ex = await openEditor(window, strip, /^Insert 6 on Audio 1: Expander/, /Audio 1: Expander/);
    await expect(ex.getByTestId('expander-curve')).toBeVisible();
    await ex.getByRole('button', { name: /^Close/ }).click();
    await addInsert(window, second, 'Utility', /^Test Oscillator/);
    const os = await openEditor(window, second, /^Insert 3 on Audio 2: Test Oscillator/, /Audio 2: Test Oscillator/);
    await expect(os.getByRole('img', { name: 'Waveform: Sine' })).toBeVisible();
    await os.getByRole('button', { name: /^Close/ }).click();
    await addInsert(window, second, 'Utility', /^Frequency Spread/);
    const sp = await openEditor(window, second, /^Insert 4 on Audio 2: Frequency Spread/, /Audio 2: Frequency Spread/);
    await expect(sp.getByTestId('spread-band')).toHaveCount(8);
    await sp.getByRole('button', { name: /^Close/ }).click();
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
