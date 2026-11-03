import { ApiError, type EditCommand, type ServerMessage } from '@anode/api-client';
import { FakeAnodeServer } from './fake-server';

async function status(run: () => Promise<unknown>): Promise<number> {
  try {
    await run();
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

async function server(): Promise<FakeAnodeServer> {
  const fake = new FakeAnodeServer();
  await fake.createProject({ name: 'Song' });
  return fake;
}

const run = (fake: FakeAnodeServer, ...commands: EditCommand[]) => fake.applyCommands({ commands, label: 'Edit' });
const environment = (fake: FakeAnodeServer) => fake.current?.environment ?? { layers: [], objects: [], cables: [] };
const objectId = (fake: FakeAnodeServer, name: string) =>
  environment(fake).objects.find(o => o.name === name)?.id ?? '';

/** Physical input → transformer → monitor → sequencer input, and a keyboard into the transformer. */
async function patch(fake: FakeAnodeServer): Promise<void> {
  await run(
    fake,
    { type: 'addEnvironmentObject', kind: 'physicalInput', x: 0, y: 0 },
    { type: 'addEnvironmentObject', kind: 'keyboard', x: 0, y: 100 },
    {
      type: 'addEnvironmentObject',
      kind: 'transformer',
      x: 200,
      y: 0,
      conditions: [{ field: 'status', op: 'equal', a: 144, b: 0 }],
      operations: [{ field: 'pitch', op: 'add', a: 12, b: 0 }]
    },
    { type: 'addEnvironmentObject', kind: 'monitor', x: 400, y: 0 },
    { type: 'addEnvironmentObject', kind: 'sequencerInput', x: 600, y: 0 }
  );
  const id = (name: string) => objectId(fake, name);
  await run(
    fake,
    {
      type: 'connectEnvironment',
      fromObjectId: id('Physical Input'),
      outlet: 0,
      toObjectId: id('Transformer'),
      inlet: 0
    },
    { type: 'connectEnvironment', fromObjectId: id('Keyboard'), outlet: 0, toObjectId: id('Transformer'), inlet: 0 },
    { type: 'connectEnvironment', fromObjectId: id('Transformer'), outlet: 0, toObjectId: id('Monitor'), inlet: 0 },
    { type: 'connectEnvironment', fromObjectId: id('Monitor'), outlet: 0, toObjectId: id('Sequencer Input'), inlet: 0 }
  );
}

describe('FakeAnodeServer, the Environment', () => {
  it('adds objects to a first layer made for them, with their kind’s defaults', async () => {
    const fake = await server();
    await run(fake, { type: 'addEnvironmentObject', kind: 'fader', x: 10, y: 20, parameters: { number: 11 } });
    const env = environment(fake);
    expect(env.layers).toHaveLength(1);
    expect(env.layers[0]?.id).toMatch(/^env_/);
    expect(env.layers[0]?.name).toBe('Layer 1');
    expect(env.objects[0]?.id).toMatch(/^eob_/);
    expect(env.objects[0]).toMatchObject({
      layerId: env.layers[0]?.id,
      name: 'Fader',
      x: 10,
      y: 20,
      parameters: { message: 0, channel: 1, number: 11, min: 0, max: 127, value: 0 }
    });
    expect(await fake.environment()).toEqual(env);
  });

  it('gives the arpeggiator its MIDI FX’s parameters, checked', async () => {
    const fake = await server();
    await run(fake, { type: 'addEnvironmentObject', kind: 'arpeggiator', x: 0, y: 0, parameters: { rate: 3 } });
    expect(environment(fake).objects[0]?.parameters).toMatchObject({ rate: 3, mode: 0, octaves: 1 });
    const id = environment(fake).objects[0]?.id ?? '';
    expect(
      await status(() => run(fake, { type: 'setEnvironmentObject', objectId: id, parameters: { rate: 99 } }))
    ).toBe(422);
    expect(await status(() => run(fake, { type: 'setEnvironmentObject', objectId: id, parameters: { nope: 1 } }))).toBe(
      422
    );
  });

  it('connects, refusing duplicates, cycles and inlets or outlets the kinds lack', async () => {
    const fake = await server();
    await patch(fake);
    const id = (name: string) => objectId(fake, name);
    expect(environment(fake).cables).toHaveLength(4);
    const refused: EditCommand[] = [
      {
        type: 'connectEnvironment',
        fromObjectId: id('Monitor'),
        outlet: 0,
        toObjectId: id('Sequencer Input'),
        inlet: 0
      },
      { type: 'connectEnvironment', fromObjectId: id('Monitor'), outlet: 0, toObjectId: id('Transformer'), inlet: 0 },
      { type: 'connectEnvironment', fromObjectId: id('Monitor'), outlet: 0, toObjectId: id('Monitor'), inlet: 0 },
      { type: 'connectEnvironment', fromObjectId: id('Transformer'), outlet: 2, toObjectId: id('Monitor'), inlet: 0 },
      { type: 'connectEnvironment', fromObjectId: id('Transformer'), outlet: 1, toObjectId: id('Keyboard'), inlet: 0 },
      {
        type: 'connectEnvironment',
        fromObjectId: id('Sequencer Input'),
        outlet: 0,
        toObjectId: id('Monitor'),
        inlet: 0
      }
    ];
    for (const command of refused) {
      expect(await status(() => run(fake, command))).toBe(422);
    }
    // Fan-out and merges are fine.
    await run(fake, {
      type: 'connectEnvironment',
      fromObjectId: id('Transformer'),
      outlet: 1,
      toObjectId: id('Sequencer Input'),
      inlet: 0
    });
    expect(environment(fake).cables).toHaveLength(5);
  });

  it('keeps one sequencer input and one physical input for every input', async () => {
    const fake = await server();
    await patch(fake);
    expect(await status(() => run(fake, { type: 'addEnvironmentObject', kind: 'sequencerInput', x: 0, y: 0 }))).toBe(
      422
    );
    expect(await status(() => run(fake, { type: 'addEnvironmentObject', kind: 'physicalInput', x: 0, y: 0 }))).toBe(
      422
    );
    await run(fake, { type: 'addEnvironmentObject', kind: 'physicalInput', x: 0, y: 0, port: 'in:1', name: 'Keys' });
    const keys = objectId(fake, 'Keys');
    // Its port cleared, it would be a second physical input for every input.
    expect(await status(() => run(fake, { type: 'setEnvironmentObject', objectId: keys, port: '' }))).toBe(422);
  });

  it('checks each kind’s settings and clears them with empty values', async () => {
    const fake = await server();
    await run(fake, { type: 'addTrack', kind: 'instrument', stereo: true });
    const trackId = fake.current?.tracks[0]?.id ?? '';
    await run(
      fake,
      { type: 'addEnvironmentObject', kind: 'trackInput', x: 0, y: 0, trackId },
      { type: 'addEnvironmentObject', kind: 'delayLine', x: 0, y: 0 },
      { type: 'addEnvironmentObject', kind: 'chordMemorizer', x: 0, y: 0, chord: [0, 4, 7] }
    );
    const [input, delay, chord] = environment(fake).objects.map(o => o.id) as [string, string, string];
    expect(environment(fake).objects[0]?.trackId).toBe(trackId);
    const bad: EditCommand[] = [
      { type: 'setEnvironmentObject', objectId: input, trackId: 'trk_404' },
      { type: 'setEnvironmentObject', objectId: input, port: 'out:1' },
      { type: 'setEnvironmentObject', objectId: delay, taps: [{ delayTicks: 0, transpose: 0, velocity: 0 }] },
      {
        type: 'setEnvironmentObject',
        objectId: delay,
        taps: Array.from({ length: 17 }, () => ({ delayTicks: 240, transpose: 0, velocity: 0 }))
      },
      {
        type: 'addEnvironmentObject',
        kind: 'transformer',
        x: 0,
        y: 0,
        conditions: [{ field: 'position', op: 'all', a: 0, b: 0 }]
      },
      {
        type: 'addEnvironmentObject',
        kind: 'transformer',
        x: 0,
        y: 0,
        operations: [{ field: 'velocity', op: 'crescendo', a: 0, b: 0 }]
      }
    ];
    for (const command of bad) {
      expect(await status(() => run(fake, command))).toBe(422);
    }
    await run(
      fake,
      { type: 'setEnvironmentObject', objectId: delay, taps: [{ delayTicks: 240, transpose: 12, velocity: -10 }] },
      { type: 'setEnvironmentObject', objectId: input, trackId: '' },
      { type: 'setEnvironmentObject', objectId: chord, chord: [] }
    );
    const objects = environment(fake).objects;
    expect(objects[0]?.trackId).toBeNull();
    expect(objects[1]?.taps).toEqual([{ delayTicks: 240, transpose: 12, velocity: -10 }]);
    expect(objects[2]?.chord).toBeNull();
  });

  it('removes objects and layers with their cables; renames and orders layers', async () => {
    const fake = await server();
    await patch(fake);
    await run(fake, { type: 'removeEnvironmentObjects', objectIds: [objectId(fake, 'Monitor')] });
    expect(environment(fake).cables).toHaveLength(2);
    await run(fake, { type: 'addEnvironmentLayer' }, { type: 'addEnvironmentLayer', name: 'Pads' });
    const [first, second, pads] = environment(fake).layers;
    expect(second?.name).toBe('Layer 2');
    await run(fake, { type: 'setEnvironmentLayer', layerId: pads?.id ?? '', name: 'Drums', index: 0 });
    expect(environment(fake).layers.map(l => l.name)).toEqual(['Drums', 'Layer 1', 'Layer 2']);
    await run(fake, {
      type: 'setEnvironmentObject',
      objectId: objectId(fake, 'Keyboard'),
      layerId: second?.id ?? null,
      x: 5
    });
    await run(fake, { type: 'removeEnvironmentLayer', layerId: first?.id ?? '' });
    const env = environment(fake);
    expect(env.objects.map(o => o.name)).toEqual(['Keyboard']);
    expect(env.cables).toEqual([]);
    expect(await status(() => run(fake, { type: 'disconnectEnvironment', cableIds: ['ecb_404'] }))).toBe(422);
  });

  it('is undoable and sends the whole document as its delta', async () => {
    const fake = await server();
    const messages: ServerMessage[] = [];
    fake.subscribe(m => messages.push(m));
    await run(fake, { type: 'addEnvironmentObject', kind: 'monitor', x: 0, y: 0 });
    expect(
      messages.some(m => m.type === 'projectChanged' && m.delta.ops.some(op => op.op === 'environmentChanged'))
    ).toBe(true);
    await fake.undo();
    expect(environment(fake).objects).toEqual([]);
  });

  it('plays a keyboard’s live MIDI through the graph: monitors report, the sequencer input records', async () => {
    const fake = await server();
    await run(fake, { type: 'addTrack', kind: 'instrument', stereo: true });
    const trackId = fake.current?.tracks[0]?.id ?? '';
    await run(fake, { type: 'setTrack', trackId, recordArmed: true, input: null });
    await patch(fake);
    const messages: ServerMessage[] = [];
    fake.subscribe(m => messages.push(m));
    await fake.environmentInput(objectId(fake, 'Keyboard'), {
      events: [
        { status: 0x90, data1: 60, data2: 100 },
        { status: 0xb0, data1: 64, data2: 127 }
      ]
    });
    const monitor = messages.filter(m => m.type === 'environmentMonitor');
    expect(monitor.at(-1)).toEqual({
      type: 'environmentMonitor',
      objectId: objectId(fake, 'Monitor'),
      events: [{ status: 0x90, data1: 72, data2: 100, sample: -1 }]
    });
    expect(fake.liveMidi.at(-1)).toEqual({ trackId, events: [{ status: 0x90, data1: 72, data2: 100 }] });
    expect(await status(() => fake.environmentInput(objectId(fake, 'Monitor'), { events: [] }))).toBe(422);
    expect(await status(() => fake.environmentInput('eob_404', { events: [] }))).toBe(404);
  });

  it('moves a fader by live MIDI and reports its value, sent on scaled to its range', async () => {
    const fake = await server();
    await run(
      fake,
      { type: 'addEnvironmentObject', kind: 'fader', x: 0, y: 0, parameters: { number: 7, max: 100 } },
      { type: 'addEnvironmentObject', kind: 'monitor', x: 200, y: 0 }
    );
    await run(fake, {
      type: 'connectEnvironment',
      fromObjectId: objectId(fake, 'Fader'),
      outlet: 0,
      toObjectId: objectId(fake, 'Monitor'),
      inlet: 0
    });
    const messages: ServerMessage[] = [];
    fake.subscribe(m => messages.push(m));
    await fake.environmentInput(objectId(fake, 'Fader'), { events: [{ status: 0xb0, data1: 7, data2: 127 }] });
    expect(messages).toContainEqual({ type: 'environmentFader', objectId: objectId(fake, 'Fader'), value: 127 });
    const shown = messages.filter(m => m.type === 'environmentMonitor').at(-1);
    expect(shown?.type === 'environmentMonitor' ? shown.events[0] : null).toMatchObject({
      status: 0xb0,
      data1: 7,
      data2: 100
    });
  });
});
