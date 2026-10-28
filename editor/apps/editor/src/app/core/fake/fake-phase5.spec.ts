import { ApiError, type AudioRegion, type EditCommand, type Project, type ServerMessage } from '@anode/api-client';
import { TempoMap } from '../../music/tempo-map';
import { timingOf } from './fake-flex';
import { FakeAnodeServer } from './fake-server';

const Beat = 960;

/** A project with an audio track holding a region of a "100 bpm" file (a beat is 28 800 frames at 48 kHz). */
async function loopProject(
  name = 'drums 100bpm.wav'
): Promise<{ server: FakeAnodeServer; trackId: string; region: AudioRegion }> {
  let time = 0;
  const server = new FakeAnodeServer({ now: () => (time += 5_000) });
  await server.createProject({ name: 'Flex' });
  await server.applyCommands({ commands: [{ type: 'addTrack', stereo: true }] });
  const trackId = server.current?.tracks[0]?.id ?? '';
  const file = await server.importAudioFile(`/Users/me/Music/${name}`);
  await server.applyCommands({ commands: [{ type: 'addRegion', trackId, fileId: file.id, startTick: 0 }] });
  const region = server.current?.regions[0] as AudioRegion;
  return { server, trackId, region };
}

async function run(server: FakeAnodeServer, ...commands: EditCommand[]): Promise<Project> {
  await server.applyCommands({ commands, label: 'Test' });
  return server.current as Project;
}

async function refused(server: FakeAnodeServer, ...commands: EditCommand[]): Promise<number> {
  try {
    await server.applyCommands({ commands, label: 'Test' });
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

function messages(server: FakeAnodeServer): ServerMessage[] {
  const list: ServerMessage[] = [];
  server.subscribe(message => list.push(message));
  return list;
}

const tick = () => new Promise(resolve => setTimeout(resolve, 0));

describe('FakeAnodeServer, Phase 5', () => {
  it('analyses imports: the file tempo from its name', async () => {
    const { server, region } = await loopProject();
    const file = server.current?.audioFiles.find(f => f.id === region.fileId);
    expect(file?.tempo?.bpm).toBe(100);
    expect(file?.tempo?.beats.slice(0, 3)).toEqual([0, 28_800, 57_600]);
    const analysis = await server.fileAnalysis(region.fileId);
    expect(analysis.transients[0]).toBe(14_400);
    expect(analysis.pitch).toEqual([]);
  });

  it('turns flex on, sets markers and undoes them', async () => {
    const { server, trackId, region } = await loopProject();
    expect(await refused(server, { type: 'setFlexMarkers', regionId: region.id, markers: [] })).toBe(422);
    let project = await run(server, { type: 'setTrackFlex', trackId, on: true, mode: 'slicing' });
    expect(project.tracks[0]).toMatchObject({ flexOn: true, flexMode: 'slicing' });
    project = await run(server, {
      type: 'setFlexMarkers',
      regionId: region.id,
      markers: [{ fileFrame: 28_800, tick: 1000 }]
    });
    expect(project.regions[0]?.flexMarkers).toEqual([{ fileFrame: 28_800, tick: 1000 }]);
    // Markers out of order are kept, and ignored by the region's timing.
    project = await run(server, {
      type: 'setFlexMarkers',
      regionId: region.id,
      markers: [
        { fileFrame: 28_800, tick: 1000 },
        { fileFrame: 40_000, tick: 900 }
      ]
    });
    expect(timingOf(project, project.regions[0] as AudioRegion).knots).toHaveLength(3);
    await server.undo();
    await server.undo();
    expect(server.current?.regions[0]?.flexMarkers).toEqual([]);
  });

  it('refuses transpose without Flex or Follow, and follows the tempo with Follow on', async () => {
    const { server, region } = await loopProject();
    expect(await refused(server, { type: 'setRegionFlex', regionId: region.id, transpose: 2 })).toBe(422);
    const project = await run(server, { type: 'setRegionFlex', regionId: region.id, follow: 'on', transpose: 2 });
    const followed = project.regions[0] as AudioRegion;
    expect(followed).toMatchObject({ follow: 'on', transpose: 2 });
    // Its beats are quarter notes whatever the project tempo (120 here).
    expect(timingOf(project, followed).tickAt(4 * 28_800)).toBeCloseTo(4 * Beat);
  });

  it('quantises audio: markers on the transients, moved to the grid, flex turned on', async () => {
    const { server, region } = await loopProject();
    // At 120 bpm the 100 bpm loop's eighth notes (14 400 frames) fall off the 1/16 grid.
    const project = await run(server, {
      type: 'quantizeAudio',
      regionIds: [region.id],
      gridTicks: Beat / 4,
      strength: 1,
      swing: 0
    });
    expect(project.tracks[0]?.flexOn).toBe(true);
    const markers = project.regions[0]?.flexMarkers ?? [];
    expect(markers.length).toBeGreaterThan(3);
    for (const m of markers) {
      expect(m.tick % (Beat / 4)).toBe(0);
    }
  });

  it('edits pitch notes and makes MIDI of them', async () => {
    const { server, trackId, region } = await loopProject('melody.wav');
    const notes = [
      {
        startFrame: 4800,
        endFrame: 24_000,
        detectedCents: 6012,
        targetCents: 6000,
        drift: 1,
        vibrato: 0.5,
        formantCents: 0,
        gainDb: 0
      },
      {
        startFrame: 30_000,
        endFrame: 48_000,
        detectedCents: 6190,
        targetCents: 6400,
        drift: 1,
        vibrato: 1,
        formantCents: 0,
        gainDb: -6
      }
    ];
    expect(await refused(server, { type: 'setPitchNotes', regionId: region.id, notes })).toBe(422);
    await run(server, { type: 'setTrackFlex', trackId, on: true, mode: 'pitch' });
    let project = await run(server, { type: 'setPitchNotes', regionId: region.id, notes });
    expect(project.regions[0]?.pitchNotes).toHaveLength(2);
    project = await run(server, { type: 'flexPitchToMidi', regionId: region.id });
    expect(project.tracks[1]).toMatchObject({ kind: 'instrument', name: 'melody MIDI' });
    const midi = project.midiRegions[0];
    expect(midi?.notes.map(n => n.pitch)).toEqual([60, 64]);
    expect(midi?.notes[0]?.startTick).toBe(192); // 0.1 s at 120 bpm
    expect(midi?.notes[1]?.velocity).toBe(50);
  });

  it('adapts the project tempo to the first import in adapt mode', async () => {
    let time = 0;
    const server = new FakeAnodeServer({ now: () => (time += 5_000) });
    await server.createProject({ name: 'Adapt' });
    await run(server, { type: 'setSmartTempo', mode: 'adapt' }, { type: 'addTrack', stereo: true });
    const file = await server.importAudioFile('/Users/me/Music/loop 100bpm.wav');
    const project = await run(server, {
      type: 'addRegion',
      trackId: server.current?.tracks[0]?.id ?? '',
      fileId: file.id,
      startTick: 0
    });
    expect(project.settings.tempo).toBe(100);
    expect(project.regions[0]?.follow).toBe('off');
    // The loop's beats are now the project's beats.
    expect(new TempoMap(project.settings.tempoEvents).secondsAt(Beat)).toBeCloseTo(0.6);
  });

  it('sets and adapts to a file tempo', async () => {
    const { server, region } = await loopProject();
    let project = await run(server, {
      type: 'setFileTempo',
      fileId: region.fileId,
      tempo: { bpm: 90, variable: false, beats: [0, 32_000], downbeats: [0] }
    });
    expect(project.audioFiles[0]?.tempo?.bpm).toBe(90);
    project = await run(server, { type: 'adaptTempo', regionId: region.id });
    expect(project.settings.tempoEvents[0]).toMatchObject({ tick: 0, bpm: 90 });
    expect(
      await refused(server, {
        type: 'setFileTempo',
        fileId: region.fileId,
        tempo: { bpm: 90, variable: false, beats: [10, 5], downbeats: [] }
      })
    ).toBe(422);
  });

  it('maps beats and shapes tempo curves', async () => {
    const { server } = await loopProject();
    let project = await run(server, { type: 'mapBeats', mappings: [{ tick: 4 * Beat, sample: 115_200 }] });
    expect(new TempoMap(project.settings.tempoEvents).secondsAt(4 * Beat)).toBeCloseTo(2.4);
    project = await run(server, {
      type: 'setTempoEvents',
      events: [
        { tick: 0, bpm: 100, curve: 'ramp', shape: 0.5 },
        { tick: 8 * Beat, bpm: 140, curve: 'step', shape: 0 }
      ]
    });
    expect(project.settings.tempoEvents[0]?.shape).toBe(0.5);
    expect(await refused(server, { type: 'mapBeats', mappings: [{ tick: 4 * Beat, sample: -5 }] })).toBe(422);
  });

  it('strips silence: a region per note', async () => {
    const { server, region } = await loopProject('phrase.wav');
    const project = await run(server, {
      type: 'stripSilence',
      regionIds: [region.id],
      thresholdDb: -40,
      minTimeMs: 50,
      preAttackMs: 10,
      postReleaseMs: 20
    });
    expect(project.regions.length).toBeGreaterThan(3);
    const starts = project.regions.map(r => r.startTick).sort((a, b) => a - b);
    expect(new Set(starts).size).toBe(starts.length);
    // A threshold above the notes' level leaves nothing.
    await server.undo();
    const none = await run(server, {
      type: 'stripSilence',
      regionIds: [region.id],
      thresholdDb: -6,
      minTimeMs: 50,
      preAttackMs: 10,
      postReleaseMs: 20
    });
    expect(none.regions).toHaveLength(0);
  });

  it('keeps track alternatives: a deleted region comes back with its alternative', async () => {
    const { server, trackId, region } = await loopProject();
    let project = await run(server, { type: 'newTrackAlternative', trackId, duplicate: true });
    let track = project.tracks[0];
    expect(track?.alternatives.map(a => a.name)).toEqual(['A', 'B']);
    const copy = project.regions[0] as AudioRegion;
    expect(copy.id).not.toBe(region.id);
    project = await run(server, { type: 'deleteRegions', regionIds: [copy.id] });
    expect(project.regions).toHaveLength(0);
    const first = track?.alternatives[0]?.id ?? '';
    const second = track?.alternatives[1]?.id ?? '';
    project = await run(server, { type: 'selectTrackAlternative', trackId, alternativeId: first });
    expect(project.regions.map(r => r.id)).toEqual([region.id]);
    project = await run(server, { type: 'selectTrackAlternative', trackId, alternativeId: second });
    expect(project.regions).toHaveLength(0);
    project = await run(server, { type: 'setTrackAlternative', trackId, alternativeId: second, name: 'Sparse' });
    track = project.tracks[0];
    expect(track?.alternatives[1]?.name).toBe('Sparse');
    project = await run(server, { type: 'deleteTrackAlternative', trackId, alternativeId: second });
    expect(project.tracks[0]?.alternativeId).toBe(first);
    expect(project.regions.map(r => r.id)).toEqual([region.id]);
    expect(await refused(server, { type: 'deleteTrackAlternative', trackId, alternativeId: first })).toBe(422);
  });

  it('sets Varispeed and Smart Tempo', async () => {
    const { server } = await loopProject();
    const project = await run(
      server,
      { type: 'setVarispeed', enabled: true, mode: 'speedAndPitch', speed: 0.9 },
      { type: 'setSmartTempo', mode: 'auto', defaultFollow: 'bars', analyzeImports: false }
    );
    expect(project.settings.varispeed).toEqual({ enabled: true, mode: 'speedAndPitch', speed: 0.9 });
    expect(project.settings.smartTempo).toEqual({ mode: 'auto', defaultFollow: 'bars', analyzeImports: false });
    expect(await refused(server, { type: 'setVarispeed', speed: 1.6 })).toBe(422);
  });

  it('reports compiled samples under Varispeed', async () => {
    const { server } = await loopProject();
    await run(server, { type: 'setVarispeed', enabled: true, speed: 0.5 });
    const status = await server.controlTransport({ command: 'locate', tick: 2 * Beat });
    // Tick 1 920 is 1 s at 120 bpm; at half speed it plays 2 s in.
    expect(status.positionSamples).toBe(96_000);
  });

  it('analyses pitch as a job, then announces it', async () => {
    const { server, region } = await loopProject('melody.wav');
    const events = messages(server);
    const job = await server.analyzeFile(region.fileId);
    expect(job.kind).toBe('analyze');
    server.runJobs();
    await tick();
    expect(events.some(e => e.type === 'analysis' && e.fileId === region.fileId)).toBe(true);
    const analysis = await server.fileAnalysis(region.fileId);
    expect(analysis.notes.length).toBeGreaterThan(2);
    expect(analysis.pitch.some(p => p === null)).toBe(true);
    await expect(server.analyzeFile('aud_missing')).rejects.toMatchObject({ status: 404 });
  });

  it('bounces a track in place to a new track, muting the source', async () => {
    const { server, trackId } = await loopProject();
    const job = await server.bounceInPlace({
      trackIds: [trackId],
      destination: 'newTrack',
      source: 'mute',
      bypassEffects: false,
      includeTail: true,
      includeVolumePan: false,
      normalize: 'off',
      name: null
    });
    expect(job.kind).toBe('bounceInPlace');
    server.runJobs();
    const project = server.current as Project;
    expect(project.tracks).toHaveLength(2);
    expect(project.tracks[1]?.name).toBe('Audio 1 Bounce');
    expect(project.tracks[0]?.strip.mute).toBe(true);
    expect(project.regions.find(r => r.trackId === project.tracks[1]?.id)?.name).toBe('Audio 1 Bounce');
    await server.undo();
    expect(server.current?.tracks).toHaveLength(1);
    await expect(
      server.bounceInPlace({
        trackIds: [],
        destination: 'newTrack',
        source: 'mute',
        bypassEffects: false,
        includeTail: true,
        includeVolumePan: false,
        normalize: 'off',
        name: null
      })
    ).rejects.toMatchObject({ status: 422 });
  });

  it('processes half a region into a new file and region', async () => {
    const { server, region } = await loopProject();
    const end = timingOf(server.current as Project, region).endTick;
    const half = Math.round(end / 2);
    const job = await server.processRegions({
      regionIds: [region.id],
      chain: [
        { pluginId: 'anode.gain', parameters: { gain: 6 } },
        { pluginId: 'anode.channel-eq', parameters: null }
      ],
      startTick: half,
      endTick: null,
      includeTail: false
    });
    expect(job.kind).toBe('process');
    server.runJobs();
    const project = server.current as Project;
    expect(project.audioFiles).toHaveLength(2);
    const processed = project.regions.find(r => r.fileId === project.audioFiles[1]?.id);
    expect(processed?.startTick).toBe(half);
    expect(project.regions.find(r => r.id === region.id)?.length).toBeLessThan(region.length);
    await expect(
      server.processRegions({
        regionIds: [region.id],
        chain: [{ pluginId: 'anode.synth', parameters: null }],
        startTick: null,
        endTick: null,
        includeTail: false
      })
    ).rejects.toMatchObject({ status: 422 });
  });

  it('follows the Model on defaults, deletes and Varispeed', async () => {
    const { server, trackId, region } = await loopProject();
    // A new region takes the default follow only when its file has a tempo.
    await run(server, { type: 'setSmartTempo', defaultFollow: 'on', analyzeImports: false });
    const plain = await server.importAudioFile('/Users/me/Music/plain.wav');
    let project = await run(server, { type: 'addRegion', trackId, fileId: plain.id, startTick: 8 * Beat });
    expect(project.regions.at(-1)?.follow).toBe('off');
    project = await run(server, { type: 'addRegion', trackId, fileId: region.fileId, startTick: 16 * Beat });
    expect(project.regions.at(-1)?.follow).toBe('on');
    // Deleting the active alternative selects the next one, or the previous one when it was the last.
    await run(
      server,
      { type: 'newTrackAlternative', trackId, duplicate: false },
      { type: 'newTrackAlternative', trackId, duplicate: false }
    );
    const [a, b, c] = server.current?.tracks[0]?.alternatives.map(x => x.id) ?? [];
    await run(server, { type: 'selectTrackAlternative', trackId, alternativeId: b ?? '' });
    project = await run(server, { type: 'deleteTrackAlternative', trackId, alternativeId: b ?? '' });
    expect(project.tracks[0]?.alternativeId).toBe(c);
    project = await run(server, { type: 'deleteTrackAlternative', trackId, alternativeId: c ?? '' });
    expect(project.tracks[0]?.alternativeId).toBe(a);
    // Adapting clears the region's markers; bounce in place waits for Varispeed to be off.
    await run(server, { type: 'setTrackFlex', trackId, on: true });
    await run(server, { type: 'setFlexMarkers', regionId: region.id, markers: [{ fileFrame: 28_800, tick: 900 }] });
    project = await run(server, { type: 'adaptTempo', regionId: region.id });
    expect(project.regions.find(r => r.id === region.id)).toMatchObject({ follow: 'off', flexMarkers: [] });
    await run(server, { type: 'setVarispeed', enabled: true });
    await expect(
      server.bounceInPlace({
        trackIds: [trackId],
        destination: 'newTrack',
        source: 'mute',
        bypassEffects: false,
        includeTail: true,
        includeVolumePan: false,
        normalize: 'off',
        name: null
      })
    ).rejects.toMatchObject({ status: 422 });
  });
});
