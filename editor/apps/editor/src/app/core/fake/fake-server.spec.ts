import type { ServerMessage } from '@anode/api-client';
import { parsePeaks } from '@anode/timeline';
import { readWavHeader, silentWav } from './fake-audio';
import { FakeAnodeServer } from './fake-server';

async function withTrackAndFile(server: FakeAnodeServer) {
  await server.createProject({ name: 'Test' });
  await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false }] });
  const file = await server.importAudioFile('/audio/take.wav');
  const trackId = server.current?.tracks[0]?.id ?? '';
  return { file, trackId };
}

describe('FakeAnodeServer', () => {
  it('splits a region in two at a tick inside it, and refuses one outside', async () => {
    const server = new FakeAnodeServer();
    const { file, trackId } = await withTrackAndFile(server);
    await server.applyCommands({ commands: [{ type: 'addRegion', trackId, fileId: file.id, startTick: 960 }] });
    const regionId = server.current?.regions[0]?.id ?? '';

    const result = await server.applyCommands({ commands: [{ type: 'splitRegion', regionId, atTick: 1920 }] });

    const [left, right] = server.current?.regions ?? [];
    expect(left?.length).toBe(24_000); // a beat at 120 bpm, 48 kHz
    expect(right).toMatchObject({ startTick: 1920, fileOffset: 24_000, length: file.frames - 24_000 });
    expect(result.delta.ops.map(op => op.op)).toEqual(['regionUpserted', 'regionUpserted']);
    await expect(server.applyCommands({ commands: [{ type: 'splitRegion', regionId, atTick: 100 }] })).rejects.toThrow(
      'inside'
    );
  });

  it('answers 409 to a stale revision and 422 to nothing to undo', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Test' });
    await expect(server.applyCommands({ commands: [{ type: 'addTrack', stereo: false }] }, 5)).rejects.toMatchObject({
      status: 409
    });
    await expect(server.undo()).rejects.toMatchObject({ status: 422 });
  });

  it('folds a fader drag into one undo step', async () => {
    let now = 0;
    const server = new FakeAnodeServer({ now: () => now });
    await server.createProject({ name: 'Test' });
    for (const volumeDb of [-1, -2, -3]) {
      now += 100;
      await server.applyCommands({ commands: [{ type: 'setStrip', volumeDb }], label: 'Volume' });
    }
    now += 2_000;
    await server.applyCommands({ commands: [{ type: 'setStrip', volumeDb: -9 }], label: 'Volume' });

    expect((await server.history()).undo.map(step => step.label)).toEqual(['Volume', 'Volume']);
    await server.undo();
    expect(server.current?.master.volumeDb).toBe(-3);
    await server.undo();
    expect(server.current?.master.volumeDb).toBe(0);
  });

  it('records a take per armed track as one undoable "Record" step', async () => {
    let now = 0;
    const server = new FakeAnodeServer({ now: () => now });
    const messages: ServerMessage[] = [];
    server.subscribe(message => messages.push(message));
    const { trackId } = await withTrackAndFile(server);
    await server.applyCommands({ commands: [{ type: 'setTrack', trackId, recordArmed: true, input: null }] });

    await server.controlTransport({ command: 'record' });
    now += 2_000;
    server.advance();
    await server.controlTransport({ command: 'stop' });

    const take = server.current?.regions[0];
    expect(take).toMatchObject({ trackId, startTick: 0, length: 96_000 });
    expect((await server.history()).undo[0]?.label).toBe('Record');
    expect(messages.some(m => m.type === 'transport' && m.transport.state === 'recording')).toBe(true);
    expect(messages.some(m => m.type === 'meters')).toBe(true);
  });

  it('serves peaks in PeakFormat at the nearest kept level', async () => {
    const server = new FakeAnodeServer();
    const { file } = await withTrackAndFile(server);
    const waveform = parsePeaks(await server.peaks(file.id, 300));
    expect(waveform.framesPerPeak).toBe(256);
    expect(waveform.peakCount).toBe(Math.ceil(file.frames / 256));
  });

  it('reads an uploaded WAV’s header and refuses another sample rate', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Test' });
    const wav = new File([silentWav(4_800)], 'click.wav');
    expect(await server.uploadAudioFile(wav)).toMatchObject({ name: 'click.wav', frames: 4_800, channels: 1 });
    const other = new File([silentWav(4_800, 44_100)], 'cd.wav');
    await expect(server.uploadAudioFile(other)).rejects.toThrow('44100 Hz');
    expect(readWavHeader(new ArrayBuffer(8))).toBeNull();
  });

  it('saves to its disk, lists recent projects and reopens them', async () => {
    const server = new FakeAnodeServer();
    const project = await server.createProject({ name: 'Keep', directory: '/songs' });
    await server.applyCommands({ commands: [{ type: 'addTrack', stereo: true }] });
    await server.saveProject();
    await server.closeProject();
    expect(await server.getProject()).toBeNull();

    expect((await server.recentProjects())[0]?.path).toBe('/songs/Keep.anode');
    const reopened = await server.openProject(project.path);
    expect(reopened.tracks).toHaveLength(1);
    expect(reopened.dirty).toBe(false);
  });
});
