import { ApiError, DefaultPanner, type EditCommand } from '@anode/api-client';
import { FakeAnodeServer } from './fake-server';

async function status(run: () => Promise<unknown>): Promise<number> {
  try {
    await run();
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

// The Phase 10 contracts (docs/11-phase-10.md) before the editor workstream implements them: the fake Server refuses the
// new commands and endpoints, scene markers and imported panners, and starts projects stereo without a movie.
describe('FakeAnodeServer, Phase 10 placeholders', () => {
  it('starts projects stereo, without a movie, with markers of kind marker', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Surround' });
    expect(server.current?.settings.surroundFormat).toBe('stereo');
    expect(server.current?.movie ?? null).toBeNull();
    expect(server.current?.master.panner).toEqual(DefaultPanner);
    await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'audio', stereo: true }] });
    const strip = server.current?.tracks[0]?.strip;
    expect([strip?.inputFormat, strip?.outputFormat, strip?.panner]).toEqual(['stereo', 'stereo', DefaultPanner]);
    await server.applyCommands({ commands: [{ type: 'addMarker', name: 'Verse', tick: 0, lengthTicks: 0 }] });
    expect(server.current?.markers[0]?.kind).toBe('marker');
  });

  it('answers the new commands and endpoints 422', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Surround' });
    await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'audio', stereo: true }] });
    const trackId = server.current?.tracks[0]?.id ?? '';
    const movie = {
      path: 'Movies/Picture.mov',
      name: 'Picture',
      inProject: true,
      startTick: 0,
      offsetSeconds: 0,
      frameRate: { numerator: 25, denominator: 1 },
      timecodeStartSeconds: 0,
      durationSeconds: 10,
      frames: 250,
      width: 640,
      height: 360,
      codec: 'h264',
      audio: { codec: 'aac', channels: 2, sampleRate: 48_000 }
    };
    const commands: EditCommand[] = [
      { type: 'setSurroundFormat', format: 'surround714' },
      { type: 'setStripFormat', trackId, inputFormat: 'mono' },
      { type: 'setPanner', trackId, mode: 'object', azimuth: 30 },
      { type: 'setMovie', movie },
      { type: 'setMoviePosition', startTick: 3840 },
      { type: 'removeMovie' },
      { type: 'addSceneMarkers', ticks: [3840, 7680], replaceExisting: true },
      { type: 'addMarker', name: 'Cut', tick: 0, lengthTicks: 0, kind: 'scene' },
      {
        type: 'importTracks',
        content: {
          tracks: [{ id: 't1', name: 'Object', kind: 'audio', midiRegions: [], regions: [], panner: DefaultPanner }],
          score: null
        }
      }
    ];
    for (const command of commands) {
      expect(await status(() => server.applyCommands({ commands: [command] }))).toBe(422);
    }
    const calls: (() => Promise<unknown>)[] = [
      () => server.importMovie({ path: '/x.mov', copyIntoProject: true }),
      () => server.movie(),
      () => server.removeMovie(),
      () => server.renderMovieThumbnails({ frames: [0, 25], height: 64 }),
      () => server.movieThumbnails(64),
      () => server.detectMovieScenes({ threshold: 0.4, replaceExisting: true }),
      () => server.importMovieAudio({}),
      () => server.exportMovieAudio({ fileName: 'Picture', replace: true, codec: 'aac' }),
      () => server.exportAdm({ fileName: 'Mix', bitDepth: 24 }),
      () => server.importAdm({ path: '/x.wav' }),
      () => server.monitoring(),
      () => server.setMonitoring({ binaural: true, downmixTo: null, speakerMap: [] }),
      () => server.discovery(),
      () => server.remotePads(),
      () => server.setRemotePads({ sets: [] }),
      () => server.listenInState()
    ];
    for (const call of calls) {
      expect(await status(call)).toBe(422);
    }
  });
});
