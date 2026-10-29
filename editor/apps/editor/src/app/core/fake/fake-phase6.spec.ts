import { ApiError, type EditCommand, type ServerMessage } from '@anode/api-client';
import { unrollPattern } from '../../music/pattern';
import { FakeAnodeServer } from './fake-server';

async function status(server: FakeAnodeServer, command: EditCommand): Promise<number> {
  try {
    await server.applyCommands({ commands: [command], label: 'Test' });
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

async function run(server: FakeAnodeServer, ...commands: EditCommand[]): Promise<void> {
  await server.applyCommands({ commands, label: 'Test' });
}

/** A project at 120 bpm with an audio track, an instrument track and a synth's defaults. */
async function project(clock?: () => number): Promise<{ server: FakeAnodeServer; audio: string; synth: string }> {
  const server = new FakeAnodeServer(clock ? { now: clock } : {});
  await server.createProject({ name: 'Beats' });
  await run(
    server,
    { type: 'addTrack', kind: 'audio', stereo: true },
    { type: 'addTrack', kind: 'instrument', stereo: true }
  );
  const [audio, synth] = server.current?.tracks.map(t => t.id) ?? [];
  return { server, audio: audio ?? '', synth: synth ?? '' };
}

const project$ = (server: FakeAnodeServer) => {
  const p = server.current;
  if (!p) {
    throw new Error('No project');
  }
  return p;
};

describe('FakeAnodeServer, Phase 6', () => {
  it('starts projects and tracks with the Phase 6 defaults', async () => {
    const { server } = await project();
    const p = project$(server);
    expect(p.patternRegions).toEqual([]);
    expect(p.liveLoops).toEqual({ scenes: [], cells: [], quantize: 'bar', shown: false });
    expect(p.tracks[1]).toMatchObject({ drumMachine: null, smartControls: null, liveLoopsActive: false });
    expect(p.tracks[1]?.strip.midiEffects).toEqual([]);
  });

  describe('pattern regions', () => {
    it('adds one with the default pattern (a note row on C3), a bar long', async () => {
      const { server, synth, audio } = await project();
      await run(server, { type: 'addPatternRegion', trackId: synth, startTick: 3840, pattern: null });
      const region = project$(server).patternRegions[0];
      expect(region).toMatchObject({ trackId: synth, startTick: 3840, lengthTicks: 3840, name: 'Pattern' });
      expect(region?.pattern.rows).toHaveLength(1);
      expect(region?.pattern.rows[0]).toMatchObject({ kind: 'note', note: 60, length: 16, playMode: 'forward' });
      expect(await status(server, { type: 'addPatternRegion', trackId: audio, startTick: 0, pattern: null })).toBe(422);
    });

    it('replaces the pattern (checked), reseeds, moves, copies, deletes and converts to MIDI', async () => {
      const { server, synth } = await project();
      await run(server, { type: 'addPatternRegion', trackId: synth, startTick: 0, pattern: null });
      const region = project$(server).patternRegions[0];
      if (!region) {
        throw new Error('no region');
      }
      const row = region.pattern.rows[0];
      if (!row) {
        throw new Error('no row');
      }
      const lit = { ...row, steps: row.steps.map((s, i) => ({ ...s, on: i % 4 === 0, chance: i === 8 ? 0.5 : 1 })) };
      await run(server, { type: 'setPattern', regionId: region.id, pattern: { ...region.pattern, rows: [lit] } });
      expect(
        await status(server, { type: 'setPattern', regionId: region.id, pattern: { ...region.pattern, swing: 3 } })
      ).toBe(422);
      expect(
        await status(server, {
          type: 'setPattern',
          regionId: region.id,
          pattern: { ...region.pattern, rows: [{ ...lit, length: 3 }] }
        })
      ).toBe(422);
      await run(server, { type: 'reseedPattern', regionId: region.id, seed: 42 });
      expect(project$(server).patternRegions[0]?.pattern.seed).toBe(42);
      await run(server, { type: 'moveRegion', regionId: region.id, startTick: 7680 });
      await run(server, { type: 'copyRegions', regionIds: [region.id], offsetTicks: 3840 });
      expect(project$(server).patternRegions.map(r => r.startTick)).toEqual([7680, 11_520]);
      const copy = project$(server).patternRegions[1];
      await run(server, { type: 'deleteRegions', regionIds: [copy?.id ?? ''] });
      const kept = project$(server).patternRegions[0];
      if (!kept) {
        throw new Error('no region');
      }
      const expected = unrollPattern(kept, { keys: project$(server).settings.keys });
      await run(server, { type: 'convertPatternToMidi', regionId: kept.id });
      expect(project$(server).patternRegions).toEqual([]);
      const midi = project$(server).midiRegions[0];
      expect(midi).toMatchObject({ startTick: 7680, lengthTicks: 3840, trackId: synth });
      expect(midi?.notes.map(n => [n.startTick + 7680, n.lengthTicks, n.pitch])).toEqual(
        expected.map(n => [n.startTick, n.lengthTicks, n.pitch])
      );
    });
  });

  describe('MIDI FX slots', () => {
    it('adds, bypasses, moves and removes MIDI effects on instrument tracks only', async () => {
      const { server, synth, audio } = await project();
      await run(server, { type: 'addInsert', pluginId: 'anode.chord-trigger', trackId: synth, chain: 'midiEffects' });
      await run(server, {
        type: 'addInsert',
        pluginId: 'anode.arpeggiator',
        trackId: synth,
        chain: 'midiEffects',
        index: 0
      });
      const slots = () => project$(server).tracks.find(t => t.id === synth)?.strip.midiEffects ?? [];
      expect(slots().map(s => s.pluginId)).toEqual(['anode.arpeggiator', 'anode.chord-trigger']);
      expect(project$(server).tracks.find(t => t.id === synth)?.strip.inserts).toEqual([]);
      const arp = slots()[0]?.id ?? '';
      await run(server, {
        type: 'setInsertParameter',
        insertId: arp,
        parameterId: 'octaves',
        value: 2,
        trackId: synth,
        chain: 'midiEffects'
      });
      expect(slots()[0]?.parameters['octaves']).toBe(2);
      await run(server, { type: 'setInsert', insertId: arp, bypassed: true, trackId: synth, chain: 'midiEffects' });
      expect(slots()[0]?.bypassed).toBe(true);
      await run(server, { type: 'moveInsert', insertId: arp, index: 1, trackId: synth, chain: 'midiEffects' });
      expect(slots().map(s => s.pluginId)).toEqual(['anode.chord-trigger', 'anode.arpeggiator']);
      await run(server, { type: 'removeInsert', insertId: arp, trackId: synth, chain: 'midiEffects' });
      expect(slots()).toHaveLength(1);
      expect(
        await status(server, { type: 'addInsert', pluginId: 'anode.compressor', trackId: synth, chain: 'midiEffects' })
      ).toBe(422);
      expect(await status(server, { type: 'addInsert', pluginId: 'anode.arpeggiator', trackId: synth })).toBe(422);
      expect(
        await status(server, { type: 'addInsert', pluginId: 'anode.arpeggiator', trackId: audio, chain: 'midiEffects' })
      ).toBe(422);
    });

    it('records a slot parameter into its midiEffect lane while playing in touch', async () => {
      let time = 0;
      const { server, synth } = await project(() => time);
      await run(server, { type: 'addInsert', pluginId: 'anode.arpeggiator', trackId: synth, chain: 'midiEffects' });
      await run(server, { type: 'setAutomationMode', trackId: synth, mode: 'touch' });
      const slot = project$(server).tracks.find(t => t.id === synth)?.strip.midiEffects[0]?.id ?? '';
      await server.controlTransport({ command: 'play' });
      time += 500;
      server.advance();
      await run(server, {
        type: 'setInsertParameter',
        insertId: slot,
        parameterId: 'gate',
        value: 0.8,
        trackId: synth,
        chain: 'midiEffects'
      });
      const lane = project$(server).automation.find(l => l.target === `midiEffect:${slot}:gate`);
      expect(lane?.points[0]?.value).toBe(0.8);
      await server.controlTransport({ command: 'stop' });
    });
  });

  describe('Drum Machine Designer', () => {
    it('makes a DMD from a kit (from an installed pack), with a pad track per pad', async () => {
      const { server } = await project();
      expect((await server.patches()).some(p => p.kind === 'stack')).toBe(false);
      expect(await status(server, { type: 'createDrumMachine', kitPatchId: 'kit_electro' })).toBe(422);
      await server.installPack('/Users/anode/Downloads/TestPack.anodepack');
      const kits = (await server.patches()).filter(p => p.kind === 'stack');
      expect(kits.map(k => k.name)).toEqual(['Electro Kit', 'Hybrid Kit']);
      await run(server, { type: 'createDrumMachine', kitPatchId: 'kit_electro', name: 'Beat' });
      const p = project$(server);
      const dmd = p.tracks.find(t => t.drumMachine);
      expect(dmd).toMatchObject({ kind: 'stack', name: 'Beat' });
      expect(dmd?.drumMachine?.pads.map(pad => [pad.note, pad.name])).toEqual([
        [36, 'Kick'],
        [38, 'Snare'],
        [39, 'Clap'],
        [42, 'Closed Hat'],
        [46, 'Open Hat']
      ]);
      const pads = p.tracks.filter(t => t.parentId === dmd?.id);
      expect(pads.map(t => t.instrument?.pluginId)).toEqual([
        'anode.drum-synth',
        'anode.drum-synth',
        'anode.drum-synth',
        'anode.drum-synth',
        'anode.quick-sampler'
      ]);
      // A pattern region on it has a row per pad.
      await run(server, { type: 'addPatternRegion', trackId: dmd?.id ?? '', startTick: 0, pattern: null });
      expect(project$(server).patternRegions[0]?.pattern.rows.map(r => [r.kind, r.note])).toEqual([
        ['pad', 36],
        ['pad', 38],
        ['pad', 39],
        ['pad', 42],
        ['pad', 46]
      ]);
    });

    it('adds, changes and removes pads; removing the DMD removes its pads', async () => {
      const { server } = await project();
      await run(server, { type: 'createDrumMachine' });
      const dmdId = project$(server).tracks.find(t => t.drumMachine)?.id ?? '';
      await run(server, { type: 'addDrumPad', trackId: dmdId, note: 40, name: 'Rim' });
      await run(server, { type: 'addDrumPad', trackId: dmdId, note: 36, pluginId: 'anode.quick-sampler' });
      expect(await status(server, { type: 'addDrumPad', trackId: dmdId, note: 36 })).toBe(422);
      const dmd = () => project$(server).tracks.find(t => t.id === dmdId);
      expect(dmd()?.drumMachine?.pads.map(p => p.note)).toEqual([36, 40]);
      await run(server, {
        type: 'setDrumPad',
        trackId: dmdId,
        note: 40,
        name: 'Side Stick',
        toNote: 37,
        pluginId: 'anode.drum-synth'
      });
      const pad = dmd()?.drumMachine?.pads.find(p => p.note === 37);
      expect(pad?.name).toBe('Side Stick');
      expect(project$(server).tracks.find(t => t.id === pad?.trackId)?.name).toBe('Side Stick');
      await run(server, { type: 'removeDrumPad', trackId: dmdId, note: 37 });
      expect(project$(server).tracks.some(t => t.id === pad?.trackId)).toBe(false);
      await run(server, { type: 'removeTrack', trackId: dmdId });
      expect(project$(server).tracks.map(t => t.kind)).toEqual(['audio', 'instrument']);
    });
  });

  describe('Smart Controls', () => {
    it('maps automatically from macro hints, maps manually through graphs, and applies a control', async () => {
      const { server, synth } = await project();
      await run(server, { type: 'autoMapSmartControls', trackId: synth });
      const controls = () => project$(server).tracks.find(t => t.id === synth)?.smartControls;
      const knobs = controls()?.controls.filter(c => c.kind === 'knob') ?? [];
      expect(knobs).toHaveLength(8);
      expect(knobs.every(k => k.mappings.length === 1)).toBe(true);
      expect(knobs[0]).toMatchObject({ name: 'Cutoff', mappings: [{ target: 'instrument:filter.cutoff' }] });
      // Knob 1 to two parameters, one through an inverted curve.
      await run(server, {
        type: 'setSmartMappings',
        trackId: synth,
        controlId: 'knob1',
        mappings: [
          {
            target: 'instrument:filter.cutoff',
            min: 100,
            max: 10_100,
            invert: false,
            graph: [
              { x: 0, y: 0 },
              { x: 1, y: 1 }
            ]
          },
          {
            target: 'instrument:filter.resonance',
            min: 0,
            max: 1,
            invert: true,
            graph: [
              { x: 0, y: 0 },
              { x: 0.5, y: 0.8 },
              { x: 1, y: 1 }
            ]
          }
        ]
      });
      await run(server, { type: 'setSmartControl', trackId: synth, controlId: 'knob1', value: 0.5 });
      const parameters = project$(server).tracks.find(t => t.id === synth)?.instrument?.parameters ?? {};
      expect(parameters['filter.cutoff']).toBeCloseTo(5_100);
      expect(parameters['filter.resonance']).toBeCloseTo(0.2);
      expect(
        await status(server, {
          type: 'setSmartMappings',
          trackId: synth,
          controlId: 'knob1',
          mappings: [{ target: 'instrument:nope', min: 0, max: 1, invert: false, graph: [] }]
        })
      ).toBe(422);
      expect(await status(server, { type: 'setSmartControl', trackId: synth, controlId: 'knob1', value: 2 })).toBe(422);
    });

    it('switches layout, toggles the arpeggiator in the first MIDI FX slot, and compares without an undo step', async () => {
      const { server, synth } = await project();
      await run(server, { type: 'setSmartControls', trackId: synth, layout: 'knobs12', arpeggiator: true });
      const track = () => project$(server).tracks.find(t => t.id === synth);
      expect(track()?.smartControls?.controls).toHaveLength(16);
      expect(track()?.smartControls?.arpeggiator).toBe(true);
      expect(track()?.strip.midiEffects[0]?.pluginId).toBe('anode.arpeggiator');
      await run(server, { type: 'setSmartControls', trackId: synth, arpeggiator: false });
      expect(track()?.strip.midiEffects[0]?.bypassed).toBe(true);
      expect(track()?.smartControls?.arpeggiator).toBe(false);

      await run(server, { type: 'autoMapSmartControls', trackId: synth });
      await server.saveProject();
      const saved = track()?.smartControls?.controls[0]?.value ?? 0;
      await run(server, { type: 'setSmartControl', trackId: synth, controlId: 'knob1', value: 0.9 });
      expect(track()?.smartControls?.controls[0]).toMatchObject({ value: 0.9, savedValue: saved });
      const history = (await server.history()).undo.length;
      await run(server, { type: 'compareSmartControls', trackId: synth, on: true });
      expect(track()?.smartControls).toMatchObject({ comparing: true });
      expect(track()?.smartControls?.controls[0]).toMatchObject({ value: saved, savedValue: 0.9 });
      expect((await server.history()).undo.length).toBe(history);
      await run(server, { type: 'compareSmartControls', trackId: synth, on: false });
      expect(track()?.smartControls?.controls[0]?.value).toBe(0.9);
    });

    it('automates a control (lane smartControl:{id})', async () => {
      const { server, synth } = await project();
      await run(server, {
        type: 'setAutomation',
        trackId: synth,
        target: 'smartControl:knob1',
        points: [
          { tick: 0, value: 0, curve: 0 },
          { tick: 3840, value: 1, curve: 0 }
        ]
      });
      expect(project$(server).automation[0]?.target).toBe('smartControl:knob1');
      expect(
        await status(server, { type: 'setAutomation', trackId: synth, target: 'smartControl:knob99', points: [] })
      ).toBe(422);
    });
  });

  describe('Live Loops', () => {
    it('edits scenes and cells, shows the grid with Remix FX on the master, and activates tracks', async () => {
      const { server, synth, audio } = await project();
      await run(server, { type: 'addScene' }, { type: 'addScene', name: 'Drop' });
      const [s1, s2] = project$(server).liveLoops.scenes;
      expect(s2?.name).toBe('Drop');
      const midi = {
        id: 'x',
        trackId: synth,
        name: 'Riff',
        startTick: 960,
        lengthTicks: 7680,
        playback: { transpose: 0, velocity: 0, quantizeTicks: 0, quantizeStrength: 1, swing: 0 },
        notes: [{ id: 1, pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480 }],
        controls: [],
        muted: false
      };
      await run(server, {
        type: 'setCell',
        trackId: synth,
        sceneId: s1?.id ?? '',
        content: { audio: null, midi, pattern: null },
        playMode: 'oneShot',
        quantize: 'quarter'
      });
      const cell = project$(server).liveLoops.cells[0];
      expect(cell).toMatchObject({
        kind: 'midi',
        name: 'Riff',
        lengthTicks: 7680,
        playMode: 'oneShot',
        quantize: 'quarter'
      });
      expect(cell?.content.midi).toMatchObject({ id: cell?.id, startTick: 0 });
      await run(server, { type: 'setCell', trackId: synth, sceneId: s1?.id ?? '', content: null, gridQuantize: true });
      expect(project$(server).liveLoops.cells[0]?.quantize).toBeNull();
      expect(
        await status(server, {
          type: 'setCell',
          trackId: audio,
          sceneId: s1?.id ?? '',
          content: { audio: null, midi, pattern: null }
        })
      ).toBe(422);
      await run(server, {
        type: 'moveCell',
        cellId: cell?.id ?? '',
        trackId: synth,
        sceneId: s2?.id ?? '',
        copy: true
      });
      expect(project$(server).liveLoops.cells).toHaveLength(2);
      await run(server, { type: 'setLiveLoops', shown: true, quantize: 'twoBars' });
      expect(project$(server).liveLoops).toMatchObject({ shown: true, quantize: 'twoBars' });
      expect(project$(server).master.inserts.map(i => i.pluginId)).toEqual(['anode.remix-fx']);
      await run(server, { type: 'setLiveLoops', shown: false }, { type: 'setLiveLoops', shown: true });
      expect(project$(server).master.inserts).toHaveLength(1);
      await run(server, { type: 'setTrackActivation', trackIds: [synth], liveLoopsActive: true });
      expect(project$(server).tracks.find(t => t.id === synth)?.liveLoopsActive).toBe(true);
      await run(server, { type: 'removeScene', sceneId: s1?.id ?? '' });
      expect(project$(server).liveLoops.cells).toHaveLength(1);
      await run(server, { type: 'deleteCells', cellIds: [project$(server).liveLoops.cells[0]?.id ?? ''] });
      expect(project$(server).liveLoops.cells).toEqual([]);
    });

    it('launches cells at the quantise boundary and reports their states as cell events', async () => {
      let time = 0;
      const { server, synth } = await project(() => time);
      const events: Extract<ServerMessage, { type: 'cell' }>[] = [];
      server.subscribe(m => {
        if (m.type === 'cell') {
          events.push(m);
        }
      });
      await server.installPack('/x/TestPack.anodepack');
      await run(server, { type: 'addScene' }, { type: 'addScene' });
      const [s1, s2] = project$(server).liveLoops.scenes;
      await server.addLoop({ loopId: 'test-pack:bass-line', trackId: synth, cellSceneId: s1?.id ?? '', tick: 0 });
      await server.addLoop({ loopId: 'test-pack:synth-chords', trackId: synth, cellSceneId: s2?.id ?? '', tick: 0 });
      const [c1, c2] = project$(server).liveLoops.cells;
      await server.launchCells({ sceneId: s1?.id ?? '' });
      // Launching while stopped starts playback; the cell starts on the next bar (bar 2 at 120 bpm: 2 s).
      expect(events.map(e => [e.cellId, e.state, e.sample])).toEqual([[c1?.id, 'queued', 96_000]]);
      time += 2_100;
      server.advance();
      expect(events.at(-1)).toMatchObject({ cellId: c1?.id, state: 'playing', sample: 96_000 });
      await server.launchCells({ cellIds: [c2?.id ?? ''] });
      expect(events.slice(-2).map(e => [e.cellId, e.state, e.sample])).toEqual([
        [c1?.id, 'stopping', 192_000],
        [c2?.id, 'queued', 192_000]
      ]);
      time += 2_000;
      server.advance();
      expect(events.slice(-2).map(e => [e.cellId, e.state])).toEqual([
        [c1?.id, 'stopped'],
        [c2?.id, 'playing']
      ]);
      await server.stopCells({});
      await server.controlTransport({ command: 'stop' });
      expect(events.at(-1)).toMatchObject({ cellId: c2?.id, state: 'stopped' });
    });

    it('records a performance into regions where the cells played, with Remix FX moves as automation', async () => {
      let time = 0;
      const { server, synth } = await project(() => time);
      await server.installPack('/x/TestPack.anodepack');
      await run(server, { type: 'addScene' }, { type: 'setLiveLoops', shown: true, quantize: 'off' });
      const scene = project$(server).liveLoops.scenes[0]?.id ?? '';
      await server.addLoop({ loopId: 'test-pack:bass-line', trackId: synth, cellSceneId: scene, tick: 0 });
      const remix = project$(server).master.inserts[0]?.id ?? '';
      await server.recordLiveLoops({ on: true });
      await server.launchCells({ sceneId: scene });
      time += 4_000;
      server.advance();
      await run(server, { type: 'setInsertParameter', insertId: remix, parameterId: 'xy1.x', value: 0.2 });
      time += 2_000;
      server.advance();
      await server.recordLiveLoops({ on: false });
      const regions = project$(server).midiRegions.filter(r => r.trackId === synth);
      expect(regions).toHaveLength(1);
      expect(regions[0]?.startTick).toBeLessThan(10);
      expect(regions[0]?.lengthTicks).toBeGreaterThan(5_000);
      const lane = project$(server).automation.find(l => l.target === `insert:${remix}:xy1.x`);
      expect(lane?.trackId).toBeNull();
      expect(lane?.points[0]?.value).toBe(0.2);
      expect((await server.history()).undo[0]?.label).toBe('Record Performance');
    });

    it('records live MIDI into a cell', async () => {
      let time = 0;
      const { server, synth } = await project(() => time);
      await run(server, { type: 'addScene' });
      const scene = project$(server).liveLoops.scenes[0]?.id ?? '';
      await server.recordLiveLoops({ on: true, trackId: synth, sceneId: scene });
      time += 2_100;
      server.advance();
      await server.sendLiveMidi(synth, [{ status: 0x90, data1: 64, data2: 100 }]);
      time += 250;
      await server.sendLiveMidi(synth, [{ status: 0x80, data1: 64, data2: 0 }]);
      await server.recordLiveLoops({ on: false, trackId: synth, sceneId: scene });
      const cell = project$(server).liveLoops.cells[0];
      expect(cell).toMatchObject({ kind: 'midi', lengthTicks: 7680 });
      expect(cell?.content.midi?.notes.map(n => n.pitch)).toEqual([64]);
    });
  });

  describe('loops and packs', () => {
    it('installs, lists and removes packs; searches the index by keyword, key, scale, text and favourites', async () => {
      const server = new FakeAnodeServer();
      expect(await server.packs()).toEqual([]);
      const pack = await server.installPack('/Users/anode/Downloads/TestPack.anodepack');
      expect(pack).toMatchObject({ id: 'test-pack', name: 'Test Pack', installed: true });
      expect(pack.loopCount).toBeGreaterThan(5);
      const drums = await server.queryLoops({ tags: ['Drums'] });
      expect(drums.items.every(l => l.tags.includes('Drums'))).toBe(true);
      expect(drums.total).toBe(drums.items.length);
      expect((await server.queryLoops({ tags: ['Bass'], key: 0 })).items.map(l => l.name)).toEqual(['Bass Line']);
      expect((await server.queryLoops({ scale: 'minor' })).items.every(l => ['minor', 'both'].includes(l.scale))).toBe(
        true
      );
      expect((await server.queryLoops({ text: 'chord' })).items.map(l => l.name)).toEqual(['Synth Chords']);
      await server.setLoopFavourite('test-pack:hat-groove', true);
      expect((await server.queryLoops({ favouritesOnly: true })).items.map(l => l.id)).toEqual([
        'test-pack:hat-groove'
      ]);
      expect((await server.queryLoops({ limit: 2, offset: 1 })).items).toHaveLength(2);
      await expect(server.installPack('/x/notes.txt')).rejects.toMatchObject({ status: 422 });
      await server.removePack('test-pack');
      expect((await server.queryLoops({})).total).toBe(0);
    });

    it('adds loops following the project: audio follows tempo and key, MIDI transposed, pattern as a pattern region', async () => {
      const { server, synth } = await project();
      await server.installPack('/x/TestPack.anodepack');
      await run(
        server,
        { type: 'setTempo', bpm: 110 },
        { type: 'setKeys', events: [{ tick: 0, tonic: 2, mode: 'minor' }] }
      );
      await server.addLoop({ loopId: 'test-pack:drum-loop-100', tick: 0 });
      const p = project$(server);
      const region = p.regions[0];
      expect(region).toMatchObject({ follow: 'on', transpose: 2, name: 'Drum Loop 100' });
      expect(p.audioFiles.find(f => f.id === region?.fileId)?.tempo?.bpm).toBe(100);
      expect(p.tracks.at(-1)).toMatchObject({ kind: 'audio', name: 'Drum Loop 100' });
      await server.addLoop({ loopId: 'test-pack:bass-line', trackId: synth, tick: 3840 });
      const bass = project$(server).midiRegions[0];
      expect(bass?.startTick).toBe(3840);
      expect(bass?.notes[0]?.pitch).toBe(38);
      await server.addLoop({ loopId: 'test-pack:beat-pattern', trackId: synth, tick: 0 });
      expect(project$(server).patternRegions[0]?.pattern.rows).toHaveLength(3);
      await server.addLoop({ loopId: 'test-pack:stab', tick: 0 });
      expect(project$(server).regions.at(-1)).toMatchObject({ follow: 'off', transpose: 0 });
      await expect(
        server.addLoop({ loopId: 'test-pack:bass-line', trackId: project$(server).tracks[0]?.id ?? '', tick: 0 })
      ).rejects.toMatchObject({ status: 422 });
      expect((await server.history()).undo[0]?.label).toBe('Add Loop');
    });

    it('previews a loop in sync, and saves a region to the loop library', async () => {
      const { server, synth } = await project();
      await server.installPack('/x/TestPack.anodepack');
      await run(server, { type: 'setKeys', events: [{ tick: 0, tonic: 2, mode: 'minor' }] });
      await server.previewLoop('test-pack:drum-loop-100');
      expect(server.previewSync).toMatchObject({ tempoRatio: 1.2, transpose: 2, startOnBar: false });
      await server.previewFile(null);
      expect(server.previewSync).toBeNull();
      await run(server, { type: 'addPatternRegion', trackId: synth, startTick: 0, pattern: null });
      const id = project$(server).patternRegions[0]?.id ?? '';
      const loop = await server.addToLoopLibrary({ regionId: id, name: 'My Beat', tags: ['Drums'], oneShot: false });
      expect(loop).toMatchObject({ kind: 'pattern', packId: null, tempo: 120, beats: 4, tags: ['Drums'] });
      expect((await server.queryLoops({ text: 'my beat' })).total).toBe(1);
    });
  });
});
