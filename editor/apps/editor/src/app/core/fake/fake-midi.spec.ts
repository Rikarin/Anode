import type { MidiRegion, ServerMessage } from '@anode/api-client';
import { FakeAnodeServer } from './fake-server';

async function withInstrumentTrack(server: FakeAnodeServer, kind: 'instrument' | 'midi' = 'instrument') {
  await server.createProject({ name: 'Test' });
  await server.applyCommands({ commands: [{ type: 'addTrack', stereo: true, kind }] });
  const trackId = server.current?.tracks[0]?.id ?? '';
  await server.applyCommands({ commands: [{ type: 'addMidiRegion', trackId, startTick: 3840, lengthTicks: 3840 }] });
  const regionId = server.current?.midiRegions[0]?.id ?? '';
  return { trackId, regionId };
}

function midi(server: FakeAnodeServer, id: string): MidiRegion {
  const region = server.current?.midiRegions.find(r => r.id === id);
  if (!region) {
    throw new Error(`no ${id}`);
  }
  return region;
}

describe('FakeAnodeServer: MIDI and instruments', () => {
  it('serves a catalogue with a synth, a sampler, the T2 instruments and a CLAP instrument, and MIDI devices', async () => {
    const server = new FakeAnodeServer();
    const plugins = await server.plugins();
    expect(plugins.filter(p => p.isInstrument).map(p => p.id)).toEqual([
      'anode.synth',
      'anode.sampler',
      'anode.drum-synth',
      'anode.quick-sampler',
      'anode.fm-synth',
      'anode.drawbar-organ',
      'anode.electric-piano',
      'anode.wavetable-synth',
      'org.anode.test-sine'
    ]);
    expect(plugins[1]?.fileTypes).toEqual(['.sfz', '.wav']);
    expect(plugins[0]?.parameters.length).toBeGreaterThan(20);
    const devices = await server.midiDevices();
    expect(devices.inputs.length).toBeGreaterThan(0);
    expect(devices.outputs.length).toBeGreaterThan(0);
  });

  it('adds an instrument track with the default synth at its defaults, and an external MIDI track', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Test' });
    await server.applyCommands({
      commands: [
        { type: 'addTrack', stereo: true, kind: 'instrument' },
        { type: 'addTrack', stereo: false, kind: 'midi' }
      ]
    });
    const [inst, ext] = server.current?.tracks ?? [];
    expect(inst).toMatchObject({
      kind: 'instrument',
      name: 'Inst 1',
      instrument: { pluginId: 'anode.synth', file: null }
    });
    expect(inst?.instrument?.parameters['filter.cutoff']).toBe(2400);
    expect(ext).toMatchObject({ kind: 'midi', name: 'MIDI 1', instrument: null, midiOutput: null });
    await expect(
      server.applyCommands({ commands: [{ type: 'addTrack', stereo: false, kind: 'instrument', pluginId: 'x' }] })
    ).rejects.toMatchObject({ status: 422 });
  });

  it('sets the instrument (back to its defaults), clamps parameters and folds a knob drag into one undo step', async () => {
    let now = 0;
    const server = new FakeAnodeServer({ now: () => now });
    const { trackId } = await withInstrumentTrack(server);
    await server.applyCommands({ commands: [{ type: 'setInstrument', trackId, pluginId: 'anode.sampler' }] });
    expect(server.current?.tracks[0]?.instrument).toMatchObject({ pluginId: 'anode.sampler', name: 'Anode Sampler' });
    for (const value of [3, 12, 99]) {
      now += 100;
      await server.applyCommands({
        commands: [{ type: 'setParameter', trackId, parameterId: 'tune', value }],
        label: 'Tune'
      });
    }
    expect(server.current?.tracks[0]?.instrument?.parameters['tune']).toBe(24);
    expect((await server.history()).undo.map(s => s.label)).toEqual(['Tune', 'Edit', 'Edit', 'Edit']);
    await server.undo();
    expect(server.current?.tracks[0]?.instrument?.parameters['tune']).toBe(0);
    await expect(
      server.applyCommands({ commands: [{ type: 'setParameter', trackId, parameterId: 'nope', value: 1 }] })
    ).rejects.toMatchObject({ status: 422 });
  });

  it('loads only the files an instrument takes, relative to the project when inside it', async () => {
    const server = new FakeAnodeServer();
    const { trackId } = await withInstrumentTrack(server);
    await expect(
      server.applyCommands({ commands: [{ type: 'loadInstrumentFile', trackId, path: '/a/piano.sfz' }] })
    ).rejects.toThrow('loads no files');
    await server.applyCommands({ commands: [{ type: 'setInstrument', trackId, pluginId: 'anode.sampler' }] });
    const inside = `${server.current?.path ?? ''}/Samples/piano.sfz`;
    await server.applyCommands({ commands: [{ type: 'loadInstrumentFile', trackId, path: inside }] });
    expect(server.current?.tracks[0]?.instrument?.file).toBe('Samples/piano.sfz');
  });

  it('gives an external MIDI track an output once setMidiIo picks one', async () => {
    const server = new FakeAnodeServer();
    const { trackId } = await withInstrumentTrack(server, 'midi');
    await server.applyCommands({
      commands: [{ type: 'setMidiIo', trackId, input: null, output: { device: 'out:rev2', channel: 2 } }]
    });
    expect(server.current?.tracks[0]?.midiOutput).toEqual({ device: 'out:rev2', channel: 2 });
  });

  it('sets MIDI input and output, refusing an output on an instrument track', async () => {
    const server = new FakeAnodeServer();
    const { trackId } = await withInstrumentTrack(server);
    await server.applyCommands({
      commands: [{ type: 'setMidiIo', trackId, input: { device: 'in:iac-1', channel: 3 }, output: null }]
    });
    expect(server.current?.tracks[0]?.midiInput).toEqual({ device: 'in:iac-1', channel: 3 });
    await expect(
      server.applyCommands({
        commands: [{ type: 'setMidiIo', trackId, input: null, output: { device: 'out:iac-1', channel: 1 } }]
      })
    ).rejects.toMatchObject({ status: 422 });
  });

  it('adds, edits, quantises and deletes notes with project-unique ids, as midiRegionUpserted deltas', async () => {
    const server = new FakeAnodeServer();
    const { regionId } = await withInstrumentTrack(server);
    const result = await server.applyCommands({
      commands: [
        {
          type: 'addNotes',
          regionId,
          notes: [
            { pitch: 60, velocity: 100, channel: 1, startTick: 250, lengthTicks: 200 },
            { pitch: 64, velocity: 90, channel: 1, startTick: 470, lengthTicks: 200 }
          ]
        }
      ]
    });
    expect(result.delta.ops.map(op => op.op)).toEqual(['midiRegionUpserted']);
    const [a, b] = midi(server, regionId).notes;
    expect(a?.id).not.toBe(b?.id);
    await server.applyCommands({
      commands: [{ type: 'editNotes', regionId, changes: [{ id: b?.id ?? 0, pitch: 65, velocity: 127 }] }]
    });
    expect(midi(server, regionId).notes[1]).toMatchObject({ pitch: 65, velocity: 127 });
    await server.applyCommands({
      commands: [{ type: 'quantizeNotes', regionId, gridTicks: 240, strength: 1, swing: 0 }]
    });
    expect(midi(server, regionId).notes.map(n => n.startTick)).toEqual([240, 480]);
    await server.applyCommands({ commands: [{ type: 'deleteNotes', regionId, noteIds: [a?.id ?? 0] }] });
    expect(midi(server, regionId).notes).toHaveLength(1);
    await expect(
      server.applyCommands({
        commands: [
          { type: 'addNotes', regionId, notes: [{ pitch: 128, velocity: 1, channel: 1, startTick: 0, lengthTicks: 1 }] }
        ]
      })
    ).rejects.toMatchObject({ status: 422 });
  });

  it('moves, splits, trims and deletes MIDI regions with the shared commands', async () => {
    const server = new FakeAnodeServer();
    const { regionId } = await withInstrumentTrack(server);
    await server.applyCommands({
      commands: [
        {
          type: 'addNotes',
          regionId,
          notes: [
            { pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480 },
            { pitch: 62, velocity: 100, channel: 1, startTick: 2400, lengthTicks: 480 }
          ]
        }
      ]
    });
    await server.applyCommands({ commands: [{ type: 'moveRegion', regionId, startTick: 7680 }] });
    expect(midi(server, regionId).startTick).toBe(7680);

    // Trimming the start keeps the notes in time.
    await server.applyCommands({
      commands: [{ type: 'resizeMidiRegion', regionId, startTick: 7680 + 960, lengthTicks: 2880 }]
    });
    expect(midi(server, regionId).notes.map(n => n.startTick)).toEqual([-960, 1440]);
    await expect(
      server.applyCommands({ commands: [{ type: 'resizeRegion', regionId, startTick: 0, fileOffset: 0, length: 1 }] })
    ).rejects.toMatchObject({ status: 422 });

    await server.applyCommands({ commands: [{ type: 'splitRegion', regionId, atTick: 7680 + 1920 }] });
    const [first, second] = server.current?.midiRegions ?? [];
    expect(first).toMatchObject({ lengthTicks: 960 });
    expect(second).toMatchObject({ startTick: 9600, lengthTicks: 1920 });
    expect(second?.notes.map(n => n.startTick)).toEqual([480]);
    expect(second?.id.startsWith('mid_')).toBe(true);

    const result = await server.applyCommands({
      commands: [{ type: 'deleteRegions', regionIds: [first?.id ?? '', second?.id ?? ''] }]
    });
    expect(result.delta.ops.map(op => op.op)).toEqual(['midiRegionRemoved', 'midiRegionRemoved']);
  });

  it('keeps region kinds on their track kinds', async () => {
    const server = new FakeAnodeServer();
    const { regionId } = await withInstrumentTrack(server);
    await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false }] });
    const audio = server.current?.tracks[1]?.id ?? '';
    await expect(
      server.applyCommands({ commands: [{ type: 'moveRegion', regionId, startTick: 0, trackId: audio }] })
    ).rejects.toMatchObject({ status: 422 });
    await expect(
      server.applyCommands({ commands: [{ type: 'addMidiRegion', trackId: audio, startTick: 0, lengthTicks: 10 }] })
    ).rejects.toMatchObject({ status: 422 });
  });

  it('sets region playback and the merge-recording setting', async () => {
    const server = new FakeAnodeServer();
    const { regionId } = await withInstrumentTrack(server);
    await server.applyCommands({
      commands: [
        { type: 'setMidiRegion', regionId, transpose: -12, quantizeTicks: 240, swing: 0.5 },
        { type: 'setMidiRecording', merge: true }
      ]
    });
    expect(midi(server, regionId).playback).toEqual({
      transpose: -12,
      velocity: 0,
      quantizeTicks: 240,
      quantizeStrength: 1,
      swing: 0.5
    });
    expect(server.current?.settings.mergeMidiRecording).toBe(true);
    await expect(
      server.applyCommands({ commands: [{ type: 'setMidiRegion', regionId, swing: 2 }] })
    ).rejects.toMatchObject({ status: 422 });
  });

  it('plays live MIDI as activity, routes thru, and records it on an armed track as a bar-rounded region', async () => {
    let now = 0;
    const server = new FakeAnodeServer({ now: () => now });
    const messages: ServerMessage[] = [];
    server.subscribe(m => messages.push(m));
    const { trackId } = await withInstrumentTrack(server);
    await server.setMidiThru(trackId);
    expect(server.thruTrack).toBe(trackId);
    await expect(server.setMidiThru('trk_nope')).rejects.toMatchObject({ status: 404 });

    await server.applyCommands({ commands: [{ type: 'setTrack', trackId, input: null, recordArmed: true }] });
    await server.controlTransport({ command: 'record', tick: 0 });
    now += 250; // 480 ticks at 120 bpm
    await server.sendLiveMidi(trackId, [{ status: 0x90, data1: 62, data2: 99 }]);
    now += 250;
    await server.sendLiveMidi(trackId, [{ status: 0x80, data1: 62, data2: 0 }]);
    now += 2_500;
    await server.controlTransport({ command: 'stop' });

    const activity = messages.filter(m => m.type === 'midiActivity');
    expect(activity).toHaveLength(2);
    expect(activity[0]).toMatchObject({ events: [{ trackId, status: 0x90, data1: 62 }] });
    const take = server.current?.midiRegions.at(-1);
    expect(take).toMatchObject({ trackId, startTick: 0, lengthTicks: 2 * 3840 });
    expect(take?.notes).toMatchObject([{ pitch: 62, velocity: 99, startTick: 480, lengthTicks: 480 }]);
    expect((await server.history()).undo[0]?.label).toBe('Record');
  });

  it('merges a take into the region under the start of the pass when asked to, faking notes from a device', async () => {
    let now = 0;
    const server = new FakeAnodeServer({ now: () => now });
    const { trackId, regionId } = await withInstrumentTrack(server);
    await server.applyCommands({
      commands: [
        { type: 'setTrack', trackId, input: null, recordArmed: true },
        { type: 'setMidiRecording', merge: true }
      ]
    });
    await server.controlTransport({ command: 'record', tick: 3840 });
    now += 1_000;
    await server.controlTransport({ command: 'stop' });
    expect(server.current?.midiRegions).toHaveLength(1);
    expect(midi(server, regionId).notes.map(n => n.pitch)).toEqual([60, 64]);
  });

  it('refuses live MIDI on an audio track and tells clients when plugins and devices change', async () => {
    const server = new FakeAnodeServer();
    const messages: ServerMessage[] = [];
    server.subscribe(m => messages.push(m));
    await server.createProject({ name: 'Test' });
    await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false }] });
    const audio = server.current?.tracks[0]?.id ?? '';
    await expect(server.sendLiveMidi(audio, [{ status: 0x90, data1: 60, data2: 1 }])).rejects.toMatchObject({
      status: 422
    });
    await server.rescanPlugins();
    await new Promise(resolve => setTimeout(resolve, 0));
    server.setMidiDevices({ inputs: [], outputs: [] });
    expect(messages.map(m => m.type)).toContain('pluginsChanged');
    expect(messages.map(m => m.type)).toContain('midiDevicesChanged');
  });
});
