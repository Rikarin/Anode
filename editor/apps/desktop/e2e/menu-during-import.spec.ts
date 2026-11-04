import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

/** A mono 16-bit WAV of a 220 Hz tone: long enough that its analysis outlasts picking from a menu. */
function writeToneWav(path: string, seconds: number, sampleRate = 48_000): void {
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

// Issue #7: the import's analysis held the Server's session, so an edit picked from a menu meanwhile waited for it, and
// was then refused as stale (the menu had closed and nothing had happened).
test('an edit picked from a menu while an import is analysed goes through', async ({ app, window }) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-menu-import-'));
  const wav = join(scratch, 'tone.wav');
  writeToneWav(wav, 240);
  try {
    await window.getByTestId('new-project-name').fill('Menu');
    await window.getByTestId('new-project-folder').fill(scratch);
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Menu');
    await window.getByTestId('add-track').click();
    await window.getByRole('group', { name: 'Track 1: Audio 1' }).click();
    await app.evaluate(({ dialog }, path) => {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
    }, wav);

    await window.getByRole('button', { name: 'Import audio' }).click();
    await window.getByTestId('add-track-menu').click();
    await window.getByTestId('add-instrument-track').click();
    await expect(window.getByRole('group', { name: 'Track 2: Inst 1' })).toBeVisible();
    await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
      'aria-label',
      'Tracks area: 1 region on 2 tracks'
    );
  } finally {
    rmSync(scratch, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
});
