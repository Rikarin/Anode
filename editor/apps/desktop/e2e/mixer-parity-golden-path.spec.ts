import type { ElectronApplication, Locator, Page } from '@playwright/test';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

/** A mono 16-bit WAV of a 220 Hz tone at 48 kHz. */
function writeToneWav(path: string, seconds = 4, sampleRate = 48_000): void {
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

async function mixerOf(app: ElectronApplication, window: Page, scratch: string): Promise<[Locator, Locator]> {
  const wav = join(scratch, 'tone.wav');
  writeToneWav(wav);
  await window.getByTestId('new-project-name').fill('Mixer Parity');
  await window.getByTestId('new-project-folder').fill(scratch);
  await window.getByTestId('create-project').click();
  await expect(window.getByTestId('project-name')).toHaveText('Mixer Parity');
  await window.getByTestId('add-track').click();
  await window.getByRole('group', { name: 'Track 1: Audio 1' }).click();
  await app.evaluate(({ dialog }, path) => {
    dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
  }, wav);
  await window.getByRole('button', { name: 'Import audio' }).click();
  await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
    'aria-label',
    'Tracks area: 1 region on 1 track',
    { timeout: 30_000 } // the import analyses the file's tempo first
  );
  await window.getByTestId('add-track').click();
  await window.locator('body').click({ position: { x: 5, y: 300 } });
  await window.keyboard.press('x');
  const first = window.getByRole('group', { name: 'Audio 1 channel strip' });
  const second = window.getByRole('group', { name: 'Audio 2 channel strip' });
  await expect(first).toBeVisible();
  await expect(second).toBeVisible();
  return [first, second];
}

async function addCompressor(window: Page, strip: Locator): Promise<void> {
  await strip.getByTestId('insert-empty').click();
  await window.getByRole('menuitem', { name: 'Dynamics', exact: true }).hover();
  await window
    .getByRole('menuitem', { name: /^Compressor/ })
    .first()
    .click();
  await expect(strip.getByTestId('insert-slot')).toHaveCount(1);
}

test('a strip takes its trim, polarity, delay and dual pan, and the pan law is the project’s', async ({
  app,
  window
}) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-mixer-parity-'));
  try {
    const [first] = await mixerOf(app, window, scratch);
    await first.getByTestId('strip-settings').click();
    const panel = window.getByTestId('strip-input');
    await panel.getByRole('spinbutton', { name: 'Trim in dB' }).fill('6');
    await panel.getByRole('spinbutton', { name: 'Trim in dB' }).press('Enter');
    await panel.getByTestId('strip-polarity').check();
    await panel.getByRole('spinbutton', { name: 'Delay in milliseconds' }).fill('-20');
    await panel.getByRole('spinbutton', { name: 'Delay in milliseconds' }).press('Enter');
    await window.keyboard.press('Escape');
    await expect(first.getByTestId('strip-settings')).toContainText('+6.0 dB ø -20 ms');

    // An aux is stereo: its channels pan on their own.
    await window.getByTestId('mixer-add').click();
    await window.getByTestId('new-aux').click();
    const aux = window.getByRole('group', { name: 'Aux 1 channel strip' });
    await aux.getByTestId('strip-settings').click();
    const auxPanel = window.getByTestId('strip-input');
    await auxPanel.getByTestId('strip-dual-pan').check();
    await auxPanel.getByRole('spinbutton', { name: 'Right channel pan' }).fill('0.5');
    await auxPanel.getByRole('spinbutton', { name: 'Right channel pan' }).press('Enter');
    await expect(auxPanel.getByRole('spinbutton', { name: 'Right channel pan' })).toHaveValue(/^0\.5/);
    await window.keyboard.press('Escape');
    await expect(aux.getByTestId('strip-settings')).toContainText('Dual');

    await window.getByTestId('mixer-settings').click();
    const law = window.getByRole('menuitemradio', { name: '−6 dB' });
    await law.click();
    await window.getByTestId('mixer-settings').click();
    await expect(window.getByRole('menuitemradio', { name: '−6 dB' })).toHaveAttribute('aria-checked', 'true');
    await window.keyboard.press('Escape');
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('an insert moves between strips by drag, and copies by its menu', async ({ app, window }) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-mixer-parity-'));
  try {
    const [first, second] = await mixerOf(app, window, scratch);
    await addCompressor(window, first);

    await first.getByTestId('insert-slot').dragTo(second.getByTestId('insert-empty'));
    await expect(second.getByTestId('insert-slot')).toHaveCount(1);
    await expect(first.getByTestId('insert-slot')).toHaveCount(0);

    await second.getByTestId('insert-slot').click({ button: 'right' });
    await window.getByTestId('insert-copy-to').hover();
    await window.getByRole('menuitem', { name: 'Audio 1' }).click();
    await expect(first.getByTestId('insert-slot')).toHaveCount(1);
    await expect(second.getByTestId('insert-slot')).toHaveCount(1);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('solo safe, a listen mode, the monitor section, a K-meter and the load meter work in the real app', async ({
  app,
  window
}) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-mixer-parity-'));
  try {
    const [first, second] = await mixerOf(app, window, scratch);

    // Solo safe on one strip, the solo mode for the project, and the toolbar says so.
    await second.getByTestId('strip-settings').click();
    await window.getByTestId('strip-solo-safe').check();
    await window.keyboard.press('Escape');
    await expect(second.getByTestId('strip-settings')).toContainText('Safe');
    await window.getByTestId('mixer-settings').click();
    await window.getByRole('menuitemradio', { name: 'After-Fader Listen' }).click();
    await expect(window.getByTestId('mixer-settings')).toContainText('AFL');

    // The monitor section: Dim takes 20 dB off, the reference replaces the level.
    await expect(window.getByTestId('monitor-level')).toHaveText('0 dB');
    await window.getByTestId('monitor-dim').click();
    await expect(window.getByTestId('monitor-dim')).toHaveAttribute('aria-pressed', 'true');
    await expect(window.getByTestId('monitor-level')).toHaveText('-20 dB');
    await window.getByTestId('monitor-dim').click();
    await window.getByTestId('monitor-reference').click();
    await expect(window.getByTestId('monitor-level')).toHaveText('-12 dB');
    await window.getByTestId('monitor-reference').click();
    await window.getByTestId('monitor-mono').click();
    await expect(window.getByTestId('monitor-mono')).toHaveAttribute('aria-pressed', 'true');

    // A K-14 meter before the fader: the strip says so and its bars carry the mark at 0 on the K scale.
    await first.getByTestId('strip-settings').click();
    await window.getByTestId('strip-meter-type').click();
    await window.getByRole('option', { name: 'K-14' }).click();
    await window.getByTestId('strip-meter-tap').click();
    await window.getByRole('option', { name: 'Pre Fader' }).click();
    await window.keyboard.press('Escape');
    await expect(first.getByTestId('strip-settings')).toContainText('K-14 Pre');
    await expect(first.getByTestId('meter-mark').first()).toBeAttached();

    // Play: the load meter follows the engine.
    await window.locator('body').click({ position: { x: 5, y: 300 } });
    await window.keyboard.press('Space');
    const meter = window.getByTestId('performance-meter');
    await expect(meter).toHaveAttribute('aria-label', /^Audio \d+ %, disk \d+ %, \d+ overloads$/, { timeout: 10_000 });
    const cores = await window.getByTestId('core-bars').locator('> span').count();
    expect(cores).toBeGreaterThan(0);
    await meter.click();
    await expect(window.getByTestId('audio-load')).toHaveText(/^\d+ %$/);
    await expect(window.getByTestId('core-list').locator('li')).toHaveCount(cores);
    await window.getByTestId('performance-reset').click();
    await expect(window.getByTestId('overload-count')).toHaveText('0');
    await window.keyboard.press('Escape');
    await window.locator('body').click({ position: { x: 5, y: 300 } });
    await window.keyboard.press('Space');
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
