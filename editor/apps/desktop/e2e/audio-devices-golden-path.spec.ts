import type { ElectronApplication, Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test } from './fixtures';

/**
 * Settings ▸ Audio ▸ Devices against the real main process and anode-host, which a unit test cannot reach: the switch
 * restarts the host, and a device that cannot open must leave the running one alone. It runs on the null device, so it
 * needs no sound card; the uid that cannot open goes through the bridge, since no list offers it.
 */
/** The preload's bridge, as the page sees it. */
interface Bridge {
  anode: { audio: { apply(settings: unknown): Promise<unknown>; settings(): Promise<unknown> } };
}

async function openSettings(app: ElectronApplication, window: Page): Promise<void> {
  await app.evaluate(({ BrowserWindow }) => {
    BrowserWindow.getAllWindows()[0]?.webContents.send('anode:openSettings');
  });
  await expect(window.getByTestId('audio-devices')).toBeVisible();
}

const savedAudio = async (app: ElectronApplication) => {
  const folder = await app.evaluate(({ app: electron }) => electron.getPath('userData'));
  return (JSON.parse(readFileSync(join(folder, 'settings.json'), 'utf8')) as { audio: { sampleRate: number } }).audio;
};

test('applies a rate and buffer live and shows what runs', async ({ app, window }) => {
  await openSettings(app, window);
  await expect(window.getByTestId('audio-stream')).toContainText('48 kHz, 128 samples');
  await expect(window.getByTestId('audio-no-latency')).toBeVisible(); // the null device has none

  await window.getByTestId('audio-rate').selectOption('44100');
  await window.getByTestId('audio-buffer').selectOption('256');
  await window.getByTestId('audio-apply').click();

  await expect(window.getByTestId('audio-stream')).toContainText('44.1 kHz, 256 samples', { timeout: 30_000 });
  await expect(window.getByTestId('engine-status')).toHaveAttribute('data-state', 'connected');
  await expect(window.getByTestId('audio-apply')).toHaveText('Apply');
  await expect(window.getByTestId('audio-error')).toBeHidden();
  expect((await savedAudio(app)).sampleRate).toBe(44_100);
});

test('a device that cannot open leaves the engine running and the settings unsaved', async ({ app, window }) => {
  await openSettings(app, window);
  await expect(window.getByTestId('audio-stream')).toContainText('48 kHz, 128 samples');

  const result = await window.evaluate(settings => (globalThis as unknown as Bridge).anode.audio.apply(settings), {
    device: 'no-such-device',
    inputDevice: null,
    sampleRate: 44_100,
    bufferFrames: 256
  });

  expect(result).toMatchObject({ applied: false, restored: true });
  expect(result).toHaveProperty('error', expect.stringMatching(/\S/));
  await expect(window.getByTestId('engine-status')).toHaveAttribute('data-state', 'connected', { timeout: 30_000 });
  await expect(window.getByTestId('audio-stream')).toContainText('48 kHz, 128 samples');
  expect(await window.evaluate(() => (globalThis as unknown as Bridge).anode.audio.settings())).toMatchObject({
    device: null,
    sampleRate: 48_000
  });

  // The engine is really back: another switch works, and the failed one's restart did not count against it.
  await window.getByTestId('audio-buffer').selectOption('512');
  await window.getByTestId('audio-apply').click();
  await expect(window.getByTestId('audio-stream')).toContainText('512 samples', { timeout: 30_000 });
});

test.describe('with a saved device that is gone', () => {
  test.use({ desktopSettings: { audio: { device: 'no-such-device', sampleRate: 48_000, bufferFrames: 128 } } });

  test('Settings can still move the engine to another device', async ({ app, window }) => {
    await expect(window.getByTestId('engine-status')).toHaveAttribute('data-state', 'disconnected');
    await openSettings(app, window);
    await expect(window.getByTestId('audio-output')).toContainText('no-such-device (not connected)');

    await window.getByTestId('audio-output').selectOption('anode:none');
    await window.getByTestId('audio-apply').click();

    await expect(window.getByTestId('engine-status')).toHaveAttribute('data-state', 'connected', { timeout: 30_000 });
    await expect(window.getByTestId('audio-stream')).toContainText('48 kHz, 128 samples');
  });
});
