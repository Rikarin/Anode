import { TestBed } from '@angular/core/testing';
import {
  type AudioRegion,
  DefaultPanner,
  EmptyChordTrack,
  EmptyEnvironment,
  EmptyProjectScore,
  type Project,
  type ProjectDelta,
  type Track
} from '@anode/api-client';
import { EventStream } from '../core/events';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { Notify } from '../core/notify';
import { GapGraceMs, ProjectStore } from './project.store';

function track(id: string, name = id): Track {
  return {
    id,
    name,
    kind: 'audio',
    color: '#e5484d',
    input: { channel: 1, stereo: false },
    recordArmed: false,
    inputMonitoring: false,
    strip: {
      volumeDb: 0,
      pan: 0,
      mute: false,
      solo: false,
      inserts: [],
      sends: [],
      output: 'master',
      groups: [],
      vcaId: null,
      midiEffects: [],
      inputFormat: 'stereo',
      outputFormat: 'stereo',
      panner: DefaultPanner
    },
    instrument: null,
    midiInput: { device: null, channel: 0 },
    midiOutput: null,
    parentId: null,
    collapsed: false,
    notes: '',
    inputBus: 0,
    automationMode: 'read',
    frozenFileId: null,
    flexOn: false,
    flexMode: 'polyphonic',
    alternatives: [],
    alternativeId: null,
    drumMachine: null,
    smartControls: null,
    liveLoopsActive: false,
    matchGroove: false
  };
}

function region(id: string, trackId: string, startTick = 0): AudioRegion {
  return {
    id,
    trackId,
    fileId: 'aud_1',
    name: id,
    startTick,
    fileOffset: 0,
    length: 48_000,
    gainDb: 0,
    fadeIn: 0,
    fadeOut: 0,
    fadeInCurve: 0,
    fadeOutCurve: 0,
    muted: false,
    follow: 'off',
    flexMarkers: [],
    transpose: 0,
    fineTune: 0,
    pitchNotes: [],
    pitchSource: 'off',
    loopTicks: 0,
    key: null
  };
}

function snapshot(revision = 3): Project {
  return {
    id: 'prj_1',
    name: 'Song',
    path: '/songs/Song.anode',
    revision,
    dirty: false,
    sampleRate: 48_000,
    settings: {
      tempo: 120,
      signature: { numerator: 4, denominator: 4 },
      cycle: { enabled: false, startTick: 0, endTick: 15_360 },
      metronome: { enabled: false, countIn: false, levelDb: -6 },
      endTick: 122_880,
      mergeMidiRecording: false,
      tempoEvents: [{ tick: 0, bpm: 120, curve: 'step', shape: 0 }],
      signatures: [{ tick: 0, numerator: 4, denominator: 4 }],
      keys: [{ tick: 0, tonic: 0, mode: 'major' }],
      recording: { cycleMode: 'takeFolder', replace: false, autopunch: false, punchInTick: 0, punchOutTick: 15_360 },
      mixer: { pdc: 'all', lowLatency: false, lowLatencyLimitMs: 10 },
      smartTempo: { mode: 'keep', defaultFollow: 'off', analyzeImports: true },
      varispeed: { enabled: false, mode: 'speedOnly', speed: 1 },
      sync: { source: 'internal', destinations: [], frameRate: 'fps25', startOffsetSeconds: 0, linkStartStop: false },
      surroundFormat: 'stereo'
    },
    master: {
      volumeDb: 0,
      pan: 0,
      mute: false,
      solo: false,
      inserts: [],
      sends: [],
      output: 'master',
      groups: [],
      vcaId: null,
      midiEffects: [],
      inputFormat: 'stereo',
      outputFormat: 'stereo',
      panner: DefaultPanner
    },
    tracks: [track('trk_1'), track('trk_2')],
    regions: [region('rgn_1', 'trk_1')],
    audioFiles: [
      {
        id: 'aud_1',
        name: 'a.wav',
        path: 'Audio Files/a.wav',
        sampleRate: 48_000,
        channels: 1,
        bitDepth: 24,
        frames: 96_000,
        tempo: null,
        version: 0
      }
    ],
    midiRegions: [],
    takeFolders: [],
    markers: [],
    arrangement: [],
    notes: '',
    groups: [],
    automation: [],
    patternRegions: [],
    liveLoops: { scenes: [], cells: [], quantize: 'bar', shown: false },
    grooves: [],
    articulationSets: [],
    laneSets: [],
    score: structuredClone(EmptyProjectScore),
    movie: null,
    chordTrack: structuredClone(EmptyChordTrack),
    sessionPlayerRegions: [],
    environment: structuredClone(EmptyEnvironment),
    offline: { files: [], unreadableTracks: [] }
  };
}

const delta = (revision: number, ops: ProjectDelta['ops']): ProjectDelta => ({ revision, ops });

describe('ProjectStore deltas', () => {
  let store: ProjectStore;

  beforeEach(() => {
    setUpFakeServer();
    store = TestBed.inject(ProjectStore);
    store.load(snapshot());
  });

  it('normalises a snapshot into maps and the track order', () => {
    expect(store.trackList().map(t => t.id)).toEqual(['trk_1', 'trk_2']);
    expect(store.regions().get('rgn_1')?.trackId).toBe('trk_1');
    expect(store.files().has('aud_1')).toBe(true);
    expect(store.revision()).toBe(3);
    expect(store.dirty()).toBe(false);
    expect(store.framesPerTick()).toBe(25); // 48 kHz at 120 bpm: 24,000 frames a beat
  });

  it('applies the next revision: upserts, removals, order, settings, master and name', () => {
    const outcome = store.apply(
      delta(4, [
        { op: 'trackUpserted', track: track('trk_3', 'Keys'), index: 0 },
        { op: 'trackRemoved', id: 'trk_1' },
        { op: 'regionUpserted', region: region('rgn_2', 'trk_2', 960) },
        { op: 'settingsChanged', settings: { ...snapshot().settings, tempo: 90 } },
        {
          op: 'masterChanged',
          strip: {
            volumeDb: -3,
            pan: 0,
            mute: false,
            solo: false,
            inserts: [],
            sends: [],
            output: 'master',
            groups: [],
            vcaId: null,
            midiEffects: [],
            inputFormat: 'stereo',
            outputFormat: 'stereo',
            panner: DefaultPanner
          }
        },
        { op: 'projectRenamed', name: 'Renamed' }
      ])
    );

    expect(outcome).toBe('applied');
    expect(store.trackIds()).toEqual(['trk_3', 'trk_2']);
    expect(store.regions().has('rgn_1')).toBe(false); // went with its track
    expect(store.regions().get('rgn_2')?.startTick).toBe(960);
    expect(store.settings().tempo).toBe(90);
    expect(store.master().volumeDb).toBe(-3);
    expect(store.info()?.name).toBe('Renamed');
    expect(store.revision()).toBe(4);
    expect(store.dirty()).toBe(true);
  });

  it('applies the score content deltas (Phase 9)', () => {
    expect(store.score()).toEqual(EmptyProjectScore);
    store.apply(
      delta(4, [
        { op: 'scoreSymbolUpserted', symbol: { id: 'sym_1', kind: 'dynamic', trackId: 'trk_1', tick: 0, text: 'mf' } },
        { op: 'scoreSymbolUpserted', symbol: { id: 'sym_2', kind: 'staccato', trackId: 'trk_1', noteId: 1 } },
        { op: 'scoreSymbolRemoved', id: 'sym_1' },
        { op: 'scoreSetUpserted', set: { id: 'scs_1', name: 'All', trackIds: ['trk_1', 'trk_2'] } },
        { op: 'scoreLayoutChanged', layout: { ...EmptyProjectScore.layout, title: 'Song' } }
      ])
    );
    expect(store.score().symbols.map(s => s.id)).toEqual(['sym_2']);
    expect(store.score().sets.map(s => s.name)).toEqual(['All']);
    expect(store.score().layout.title).toBe('Song');
  });

  it('applies the movie delta (Phase 10)', () => {
    expect(store.movie()).toBeNull();
    const movie = {
      path: 'Movies/Picture.mov',
      name: 'Picture',
      inProject: true,
      startTick: 3840,
      offsetSeconds: 0,
      frameRate: { numerator: 25, denominator: 1 },
      timecodeStartSeconds: 3600,
      durationSeconds: 60,
      frames: 1500,
      width: 1920,
      height: 1080,
      codec: 'h264',
      audio: null
    };
    store.apply(delta(4, [{ op: 'movieChanged', movie }]));
    expect(store.movie()?.startTick).toBe(3840);
    store.apply(delta(5, [{ op: 'movieChanged', movie: null }]));
    expect(store.movie()).toBeNull();
  });

  it('moves a track when its index changes', () => {
    store.apply(delta(4, [{ op: 'trackUpserted', track: track('trk_2'), index: 0 }]));
    expect(store.trackIds()).toEqual(['trk_2', 'trk_1']);
  });

  it('ignores a revision it already has (an edit’s answer and its echo on /ws)', () => {
    const ops: ProjectDelta['ops'] = [{ op: 'regionRemoved', id: 'rgn_1' }];
    expect(store.apply(delta(4, ops))).toBe('applied');
    expect(store.apply(delta(4, ops))).toBe('stale');
    expect(store.apply(delta(2, ops))).toBe('stale');
    expect(store.revision()).toBe(4);
  });

  it('holds a delta that arrives ahead of a missing one and applies both in order once the gap fills', () => {
    expect(store.apply(delta(5, [{ op: 'projectRenamed', name: 'Five' }]))).toBe('waiting');
    expect(store.info()?.name).toBe('Song');
    expect(store.apply(delta(4, [{ op: 'projectRenamed', name: 'Four' }]))).toBe('applied');
    expect(store.revision()).toBe(5);
    expect(store.info()?.name).toBe('Five');
  });

  it('refetches the project when a gap does not fill', () => {
    vi.useFakeTimers();
    try {
      const refetch = vi.spyOn(store, 'refetch').mockResolvedValue();
      store.apply(delta(6, []));
      vi.advanceTimersByTime(GapGraceMs - 1);
      expect(refetch).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);
      expect(refetch).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('refetches on resync, and clears when the project is closed', () => {
    const refetch = vi.spyOn(store, 'refetch').mockResolvedValue();
    const events = TestBed.inject(EventStream);
    events.dispatch({ type: 'resync' });
    expect(refetch).toHaveBeenCalledTimes(1);
    events.dispatch({ type: 'projectClosed' });
    expect(store.isOpen()).toBe(false);
    expect(store.trackIds()).toEqual([]);
  });

  it('refetches when a region arrives for a file it does not know (imported by another client)', () => {
    const refetch = vi.spyOn(store, 'refetch').mockResolvedValue();
    store.apply(delta(4, [{ op: 'regionUpserted', region: { ...region('rgn_9', 'trk_1'), fileId: 'aud_new' } }]));
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it('clears the dirty flag on projectSaved at the revision it has', () => {
    store.apply(delta(4, []));
    const events = TestBed.inject(EventStream);
    const summary = { id: 'prj_1', name: 'Song', path: '/songs/Song.anode', modified: '' };
    events.dispatch({ type: 'projectSaved', project: summary, revision: 3 });
    expect(store.dirty()).toBe(true); // saved before the latest edit
    events.dispatch({ type: 'projectSaved', project: summary, revision: 4 });
    expect(store.dirty()).toBe(false);
  });
});

describe('ProjectStore edits against the fake Server', () => {
  it('sends a labelled batch with the revision, applies the answer, and ignores the echo', async () => {
    const server = setUpFakeServer();
    const store = TestBed.inject(ProjectStore);
    await server.createProject({ name: 'Edits' });
    await store.refetch();

    const result = await store.execute([{ type: 'addTrack', name: 'Bass', stereo: false }], 'New Track');

    expect(result?.revision).toBe(1);
    expect(store.trackList().map(t => t.name)).toEqual(['Bass']);
    expect(store.revision()).toBe(1);
    expect((await server.history()).undo[0]?.label).toBe('New Track');
  });

  it('runs edits one at a time, so each carries the revision before it', async () => {
    const server = setUpFakeServer();
    const store = TestBed.inject(ProjectStore);
    await server.createProject({ name: 'Queue' });
    await store.refetch();
    const applyCommands = vi.spyOn(server, 'applyCommands');

    await Promise.all([
      store.execute([{ type: 'addTrack', stereo: false }], 'New Track'),
      store.execute([{ type: 'addTrack', stereo: false }], 'New Track'),
      store.execute([{ type: 'addTrack', stereo: false }], 'New Track')
    ]);

    expect(applyCommands.mock.calls.map(call => call[1])).toEqual([0, 1, 2]);
    expect(store.trackIds()).toHaveLength(3);
  });

  it('reports a refused edit and refetches on a stale revision', async () => {
    const server = setUpFakeServer();
    const store = TestBed.inject(ProjectStore);
    const notify = TestBed.inject(Notify);
    await server.createProject({ name: 'Refused' });
    await store.refetch();

    expect(await store.execute([{ type: 'removeTrack', trackId: 'trk_nope' }], 'Delete Track')).toBeNull();
    expect(notify.last()).toBe('Could not delete track: No track trk_nope.');

    // Another client edits; this store has not seen it yet when it sends.
    const unsubscribe = vi.spyOn(store, 'apply').mockReturnValue('stale');
    await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false }] });
    unsubscribe.mockRestore();
    expect(await store.execute([{ type: 'addTrack', stereo: false }], 'New Track')).toBeNull();
    await settle();
    expect(notify.last()).toContain('changed elsewhere');
    expect(store.revision()).toBe(1);
    expect(store.trackIds()).toHaveLength(1);
  });

  it('undoes and redoes through the same queue', async () => {
    const server = setUpFakeServer();
    const store = TestBed.inject(ProjectStore);
    await server.createProject({ name: 'Undo' });
    await store.refetch();
    await store.execute([{ type: 'addTrack', name: 'One', stereo: false }], 'New Track');

    await store.undo();
    expect(store.trackIds()).toEqual([]);
    await store.redo();
    expect(store.trackList().map(t => t.name)).toEqual(['One']);
    expect(store.revision()).toBe(3);
  });
});
