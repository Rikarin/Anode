import {
  ApiError,
  DefaultMonitoringSettings,
  DefaultPanner,
  type EditCommand,
  ListenIn,
  type Movie,
  readListenInFrame,
  type ServerMessage
} from '@anode/api-client';
import { KeyCommands } from '../../keys/key-commands';
import { DefaultRemotePads, FakeListenInCodec } from './fake-phase10';
import { FakeAnodeServer } from './fake-server';

async function status(run: () => Promise<unknown>): Promise<number> {
  try {
    await run();
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

const Bar = 3840;

const movie: Movie = {
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

async function setUp() {
  const server = new FakeAnodeServer();
  await server.createProject({ name: 'Surround' });
  await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'audio', stereo: false, name: 'Voice' }] });
  const trackId = server.current?.tracks[0]?.id ?? '';
  const run = (...commands: EditCommand[]) => server.applyCommands({ commands });
  return { server, trackId, run, strip: () => server.current?.tracks[0]?.strip };
}

describe('FakeAnodeServer, Phase 10 (docs/11-phase-10.md)', () => {
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

  describe('formats and panners', () => {
    it('keeps output formats equal to their destinations, and the panner mode one the output allows', async () => {
      const { server, trackId, run, strip } = await setUp();
      await run({ type: 'setSurroundFormat', format: 'surround714' });
      expect(server.current?.master.inputFormat).toBe('surround714');
      expect(server.current?.master.outputFormat).toBe('surround714');
      expect(strip()?.outputFormat).toBe('surround714');
      expect(strip()?.panner.mode).toBe('surround');
      // A device output pair is stereo: the strip pans in stereo again.
      await run({ type: 'setOutput', trackId, output: 'output:1' });
      expect([strip()?.outputFormat, strip()?.panner.mode]).toEqual(['stereo', 'stereo']);
      // A bus takes its aux's format.
      await run({ type: 'addAux', bus: 3 });
      const aux = server.current?.tracks.find(t => t.kind === 'aux');
      await run({ type: 'setStripFormat', trackId: aux?.id ?? '', inputFormat: 'surround51' });
      await run({ type: 'setOutput', trackId, output: 'bus:3' });
      expect(strip()?.outputFormat).toBe('surround51');
      // Undo goes back a step at a time.
      await server.undo();
      expect(strip()?.outputFormat).toBe('stereo');
    });

    it('sets the panners flat, in range, and refuses modes the output does not allow', async () => {
      const { trackId, run, strip, server } = await setUp();
      expect(await status(() => run({ type: 'setPanner', trackId, mode: 'surround' }))).toBe(422);
      await run({ type: 'setSurroundFormat', format: 'surround714' });
      await run({ type: 'setPanner', trackId, angle: 110, diversity: 0.8, lfeLevelDb: -6 });
      expect(strip()?.panner.surround).toMatchObject({ angle: 110, diversity: 0.8, lfeLevelDb: -6, elevation: 0 });
      await run({ type: 'setPanner', trackId, mode: 'object', azimuth: 30, objectElevation: 45, size: 0.25 });
      expect(strip()?.panner.mode).toBe('object');
      expect(strip()?.panner.object).toEqual({ azimuth: 30, elevation: 45, distance: 1, size: 0.25 });
      // Both panners' settings survive mode switches.
      await run({ type: 'setPanner', trackId, mode: 'surround' });
      expect(strip()?.panner.surround.angle).toBe(110);
      expect(await status(() => run({ type: 'setPanner', trackId, angle: 200 }))).toBe(422);
      expect(await status(() => run({ type: 'setPanner', trackId, mode: 'stereo' }))).toBe(422);
      // An object needs the master as its output.
      await run({ type: 'setOutput', trackId, output: 'output:2' });
      expect(await status(() => run({ type: 'setPanner', trackId, mode: 'object' }))).toBe(422);
      expect(server.current?.tracks[0]?.strip.panner.mode).toBe('stereo');
    });

    it('takes audio and aux input formats only', async () => {
      const { server, trackId, run, strip } = await setUp();
      await run({ type: 'setStripFormat', trackId, inputFormat: 'surround51' });
      expect(strip()?.inputFormat).toBe('surround51');
      await run({ type: 'addTrack', kind: 'instrument', stereo: true });
      const instrument = server.current?.tracks.at(-1)?.id ?? '';
      expect(await status(() => run({ type: 'setStripFormat', trackId: instrument, inputFormat: 'mono' }))).toBe(422);
      expect(await status(() => run({ type: 'setSurroundFormat', format: 'surround999' as never }))).toBe(422);
    });

    it('records panner moves into surround: and object: lanes in touch while playing', async () => {
      const { server, trackId, run } = await setUp();
      await run({ type: 'setSurroundFormat', format: 'surround51' });
      await run({ type: 'setAutomationMode', trackId, mode: 'touch' });
      await server.controlTransport({ command: 'play' });
      await run({ type: 'setPanner', trackId, angle: -90 });
      await server.controlTransport({ command: 'stop' });
      const lane = server.current?.automation.find(l => l.target === 'surround:angle');
      expect(lane?.points.map(p => p.value)).toEqual([-90]);
      // Panner addresses are automation targets.
      await run({
        type: 'setAutomation',
        trackId,
        target: 'object:azimuth',
        points: [{ tick: 0, value: 30, curve: 0 }]
      });
      expect(server.current?.automation.some(l => l.target === 'object:azimuth')).toBe(true);
    });
  });

  describe('the movie', () => {
    it('imports as a job, moves, detects scene cuts as scene markers, and is removed as one undo step', async () => {
      const { server, run } = await setUp();
      const messages: ServerMessage[] = [];
      server.subscribe(m => messages.push(m));
      expect(await status(() => server.movie())).toBe(404);
      const job = await server.importMovie({ path: '/Users/anode/Movies/Picture.mov', copyIntoProject: true });
      expect(job.kind).toBe('movieImport');
      server.runJobs();
      const imported = await server.movie();
      expect(imported).toMatchObject({ path: 'Movies/Picture.mov', inProject: true, frames: 1500, startTick: 0 });
      expect(messages.some(m => m.type === 'projectChanged' && m.delta.ops.some(op => op.op === 'movieChanged'))).toBe(
        true
      );
      await run({ type: 'setMoviePosition', startTick: Bar, offsetSeconds: 0 });
      expect(server.current?.movie?.startTick).toBe(Bar);
      expect(await status(() => run({ type: 'setMoviePosition', offsetSeconds: 600 }))).toBe(422);

      await server.detectMovieScenes({ threshold: 0.4, replaceExisting: true });
      server.runJobs();
      const scenes = server.current?.markers.filter(m => m.kind === 'scene') ?? [];
      // A cut every 4 s at 120 bpm (2 bars a 4 s), the movie starting at bar 2.
      expect(scenes[0]).toMatchObject({ name: 'Scene 1', tick: Bar + 2 * Bar });
      expect(scenes).toHaveLength(14);
      // Scene markers are ordinary markers: editable, and a marker can be made a scene marker.
      await run({ type: 'addMarker', name: 'Cut', tick: 0, lengthTicks: 0, kind: 'scene' });
      expect(server.current?.markers[0]?.kind).toBe('scene');

      await server.removeMovie();
      expect(server.current?.movie).toBeNull();
      await server.undo();
      expect(server.current?.movie?.name).toBe('Picture');
      expect(await status(() => server.importMovie({ path: '/x.avi', copyIntoProject: true }))).toBe(422);
    });

    it('renders and lists thumbnails, and answers frames as images', async () => {
      const { server, run } = await setUp();
      await run({ type: 'setMovie', movie });
      await server.renderMovieThumbnails({ frames: [0, 25, 50], height: 48 });
      expect((await server.movieThumbnails(48)).frames).toEqual([]);
      server.runJobs();
      expect(await server.movieThumbnails(48)).toEqual({ width: 86, height: 48, frames: [0, 25, 50] });
      expect(server.movieFrameUrl(25, 48)).toMatch(/^data:image\/svg\+xml/);
      expect(await status(() => server.renderMovieThumbnails({ frames: [250], height: 48 }))).toBe(422);
    });

    it('imports the movie audio as a track at its first frame, and exports audio to a new movie', async () => {
      const { server, run } = await setUp();
      await run({ type: 'setMovie', movie: { ...movie, startTick: Bar, offsetSeconds: 1 } });
      await server.importMovieAudio({ trackName: 'Production Audio' });
      server.runJobs();
      const track = server.current?.tracks.at(-1);
      expect(track?.name).toBe('Production Audio');
      const region = server.current?.regions.find(r => r.trackId === track?.id);
      // The movie's first frame is 1 s (2 beats at 120 bpm) before the start position.
      expect(region?.startTick).toBe(Bar - 1920);
      const job = await server.exportMovieAudio({ fileName: 'Picture with Mix', replace: true, codec: 'aac' });
      server.runJobs();
      expect((await server.job(job.id)).files[0]).toMatch(/Exports\/Picture with Mix\.mov$/);
    });
  });

  describe('ADM BWF', () => {
    it('exports a surround project and imports a bed and objects with their automation', async () => {
      const { server, run } = await setUp();
      expect(await status(() => server.exportAdm({ fileName: 'Mix', bitDepth: 24 }))).toBe(422);
      const imported = await server.importAdm({ path: '/Users/anode/Mix.wav' });
      server.runJobs();
      expect((await server.job(imported.id)).state).toBe('done');
      expect(server.current?.settings.surroundFormat).toBe('surround714');
      const tracks = server.current?.tracks.slice(1) ?? [];
      expect(tracks.map(t => [t.strip.inputFormat, t.strip.panner.mode])).toEqual([
        ['surround714', 'surround'],
        ['mono', 'object'],
        ['mono', 'object']
      ]);
      expect(server.current?.automation.filter(l => l.trackId === tracks[1]?.id).map(l => l.target)).toEqual([
        'object:azimuth',
        'object:elevation'
      ]);
      await run({ type: 'setSurroundFormat', format: 'surround714' });
      const exported = await server.exportAdm({ fileName: 'Mix', bitDepth: 24 });
      server.runJobs();
      expect((await server.job(exported.id)).files[0]).toMatch(/Exports\/Mix\.wav$/);
    });
  });

  describe('monitoring, discovery and pads', () => {
    it('keeps monitoring and pad sets as user settings, checked', async () => {
      const { server } = await setUp();
      expect(await server.monitoring()).toEqual(DefaultMonitoringSettings);
      await server.setMonitoring({ binaural: true, downmixTo: 'stereo', speakerMap: [1, 2, 0], section: null });
      expect((await server.monitoring()).speakerMap).toEqual([1, 2, 0]);
      expect(await status(() => server.setMonitoring({ binaural: false, speakerMap: [-1], section: null }))).toBe(422);
      const pads = await server.remotePads();
      expect(pads.sets.map(s => s.name)).toEqual(['Transport', 'Editing']);
      await server.setRemotePads({ sets: [{ id: 'mine', name: 'Mine', pads: [] }] });
      expect((await server.remotePads()).sets).toHaveLength(1);
      expect(await status(() => server.setRemotePads({ sets: [{ id: 'a', name: '', pads: [] }] }))).toBe(422);
      expect(await server.discovery()).toMatchObject({ projectName: 'Surround', pairingRequired: false });
    });
  });

  describe('listen-in', () => {
    it('streams the fake master in the real framing, at most four listeners', async () => {
      const { server } = await setUp();
      const states: boolean[] = [];
      server.subscribe(m => m.type === 'listenIn' && states.push(m.state.on));
      const socket = server.openListenIn();
      const messages: (string | ArrayBuffer)[] = [];
      socket.onmessage = event => messages.push(event.data);
      await new Promise(resolve => setTimeout(resolve, 0));
      expect(JSON.parse(messages[0] as string)).toMatchObject({ sampleRate: 48_000, codec: FakeListenInCodec });
      expect((await server.listenInState()).listeners).toBe(1);
      await server.controlTransport({ command: 'play' });
      socket.pump(2);
      const frame = readListenInFrame(messages[1] as ArrayBuffer);
      expect(frame?.sequence).toBe(0);
      expect(frame?.flags).toBe(ListenIn.flagPlaying | ListenIn.flagDiscontinuity);
      expect(frame?.packet.byteLength).toBe(ListenIn.frameSamples * 4);
      expect(readListenInFrame(messages[2] as ArrayBuffer)?.flags).toBe(ListenIn.flagPlaying);
      const others = [server.openListenIn(), server.openListenIn(), server.openListenIn()];
      expect(() => server.openListenIn()).toThrow(ApiError);
      socket.close();
      others.forEach(s => s.close());
      expect((await server.listenInState()).on).toBe(false);
      expect(states.at(-1)).toBe(false);
    });
  });
});

describe('the factory pad sets', () => {
  it('run key commands the editor has (the real Server has the same sets, RemotePads.Default)', () => {
    const ids = new Set(KeyCommands.map(c => c.id));
    const missing = DefaultRemotePads.sets.flatMap(s => s.pads.map(p => p.commandId)).filter(id => !ids.has(id));
    expect(missing).toEqual([]);
  });
});
