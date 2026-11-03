import { TestBed } from '@angular/core/testing';
import { type EnvironmentObject, EnvironmentObjectKinds, type MidiEnvironment } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { EnvironmentGraphStore } from './environment-graph.store';
import {
  cableProblem,
  describeEvent,
  faderEvent,
  faderOutput,
  faderValue,
  flowFrom,
  kindLimitProblem,
  portIndex,
  tapsProblem,
  transform,
  transformProblem
} from './environment-model';
import { EnvironmentWindow } from './environment-window';

function object(
  id: string,
  kind: keyof typeof EnvironmentObjectKinds,
  extra: Partial<EnvironmentObject> = {}
): EnvironmentObject {
  return {
    id,
    layerId: 'env_1',
    kind,
    name: EnvironmentObjectKinds[kind].name,
    x: 0,
    y: 0,
    parameters: Object.fromEntries(EnvironmentObjectKinds[kind].parameters.map(p => [p.id, p.default])),
    ...extra
  };
}

/** The golden path's patch, by hand: input → transformer (+12) → arpeggiator → track input, a monitor on the transformer. */
const patch: MidiEnvironment = {
  layers: [{ id: 'env_1', name: 'Layer 1' }],
  objects: [
    object('in', 'physicalInput'),
    object('tf', 'transformer', {
      conditions: [{ field: 'status', op: 'equal', a: 144, b: 0 }],
      operations: [{ field: 'pitch', op: 'add', a: 12, b: 0 }]
    }),
    object('arp', 'arpeggiator'),
    object('trk', 'trackInput', { trackId: 'trk_1' }),
    object('mon', 'monitor'),
    object('seq', 'sequencerInput')
  ],
  cables: [
    { id: 'c1', fromObjectId: 'in', outlet: 0, toObjectId: 'tf', inlet: 0 },
    { id: 'c2', fromObjectId: 'tf', outlet: 0, toObjectId: 'arp', inlet: 0 },
    { id: 'c3', fromObjectId: 'arp', outlet: 0, toObjectId: 'trk', inlet: 0 },
    { id: 'c4', fromObjectId: 'tf', outlet: 0, toObjectId: 'mon', inlet: 0 },
    { id: 'c5', fromObjectId: 'tf', outlet: 1, toObjectId: 'seq', inlet: 0 }
  ]
};

describe('the Environment model (the Server’s rules)', () => {
  it('refuses cables to ports a kind lacks, duplicates and cycles; allows fan-out', () => {
    expect(cableProblem(patch, 'in', 0, 'mon', 0)).toBeNull();
    expect(cableProblem(patch, 'tf', 2, 'mon', 0)).toMatch(/no outlet 3/);
    expect(cableProblem(patch, 'tf', 0, 'in', 0)).toMatch(/no inlet 1/);
    expect(cableProblem(patch, 'tf', 0, 'arp', 0)).toMatch(/already/);
    expect(cableProblem(patch, 'mon', 0, 'tf', 0)).toMatch(/loop/);
    expect(cableProblem(patch, 'arp', 0, 'arp', 0)).toMatch(/loop/);
    expect(cableProblem(patch, 'nope', 0, 'arp', 0)).toMatch(/No object/);
  });

  it('keeps one sequencer input and one physical input for every input', () => {
    expect(kindLimitProblem(patch, 'sequencerInput', null)).toMatch(/Sequencer Input/);
    expect(kindLimitProblem(patch, 'sequencerInput', null, 'seq')).toBeNull();
    expect(kindLimitProblem(patch, 'physicalInput', null)).toMatch(/port/);
    expect(kindLimitProblem(patch, 'physicalInput', 'in:1')).toBeNull();
  });

  it('checks transformer rules and delay taps', () => {
    expect(transformProblem([{ field: 'position', op: 'all', a: 0, b: 0 }], [])).toMatch(/not position/);
    expect(transformProblem([], [{ field: 'status', op: 'fix', a: 0, b: 0 }])).toMatch(/not status/);
    expect(transformProblem([], [{ field: 'velocity', op: 'crescendo', a: 0, b: 0 }])).toMatch(/crescendo/);
    expect(tapsProblem([{ delayTicks: 3841, transpose: 0, velocity: 0 }])).toMatch(/3840/);
    expect(tapsProblem([{ delayTicks: 240, transpose: 12, velocity: -20 }])).toBeNull();
  });

  it('transforms and routes events: matched notes up an octave, the rest unmatched', () => {
    const tf = patch.objects[1] ?? object('tf', 'transformer');
    expect(transform(tf, { status: 0x91, data1: 60, data2: 90 })).toEqual({
      outlet: 0,
      event: { status: 0x91, data1: 72, data2: 90 }
    });
    expect(transform(tf, { status: 0xb0, data1: 1, data2: 64 }).outlet).toBe(1);
    const flow = flowFrom(patch, 'in', [
      { status: 0x90, data1: 60, data2: 100 },
      { status: 0xb0, data1: 64, data2: 127 }
    ]);
    expect(flow.monitors.get('mon')).toEqual([{ status: 0x90, data1: 72, data2: 100 }]);
    expect(flow.tracks.get('trk_1')).toEqual([{ status: 0x90, data1: 72, data2: 100 }]);
    expect(flow.sequencer).toEqual([{ status: 0xb0, data1: 64, data2: 127 }]);
  });

  it('turns fader moves into their message and back, scaled to the range', () => {
    const cc = { message: 0, channel: 2, number: 7, min: 0, max: 100 };
    expect(faderEvent(cc, 64)).toEqual({ status: 0xb1, data1: 7, data2: 64 });
    expect(faderValue(cc, { status: 0xb1, data1: 7, data2: 30 })).toBe(30);
    expect(faderValue(cc, { status: 0xb0, data1: 7, data2: 30 })).toBeNull();
    expect(faderOutput(cc, 127)).toBe(100);
    expect(faderEvent({ ...cc, message: 3 }, 64)).toEqual({ status: 0xe1, data1: 0, data2: 64 });
    expect(describeEvent({ status: 0x90, data1: 60, data2: 100 })).toBe('Note On C3 100 · 1');
    expect(portIndex('out3', 'out')).toBe(3);
    expect(portIndex('out3', 'in')).toBeNull();
  });
});

describe('EnvironmentGraphStore', () => {
  async function setUp() {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Patch' }));
    return { server, project, store: TestBed.runInInjectionContext(() => new EnvironmentGraphStore()) };
  }

  it('makes the default pair, cabled, when the Environment is empty', async () => {
    const { store, server } = await setUp();
    await store.ensureDefault();
    await settle();
    const env = store.environment();
    expect(env.objects.map(o => o.kind)).toEqual(['physicalInput', 'sequencerInput']);
    expect(env.cables).toHaveLength(1);
    expect(store.edges()).toEqual([
      expect.objectContaining({ source: { nodeId: env.objects[0]?.id, portId: 'out0' } })
    ]);
    // Once is enough.
    await store.ensureDefault();
    expect(server.current?.environment.objects).toHaveLength(2);
  });

  it('adds objects from the palette, selecting them; refuses a second sequencer input before asking', async () => {
    const { store } = await setUp();
    await store.ensureDefault();
    await store.addObject('monitor');
    const monitor = store.selectedObject();
    expect(monitor?.kind).toBe('monitor');
    expect(monitor?.x).toBeGreaterThan(300);
    expect(store.addProblem('sequencerInput')).toMatch(/Sequencer Input/);
    await store.addObject('sequencerInput');
    expect(store.problem()).toMatch(/Sequencer Input/);
  });

  it('connects either way round, refusing what the Server would before sending it', async () => {
    const { store, server } = await setUp();
    await store.ensureDefault();
    await store.addObject('monitor');
    const [input, sequencer, monitor] = store.environment().objects.map(o => o.id) as [string, string, string];
    // Drawn from the monitor's inlet back to the input's outlet.
    expect(
      await store.connect({ source: { nodeId: monitor, portId: 'in0' }, target: { nodeId: input, portId: 'out0' } })
    ).toBe(true);
    expect(store.environment().cables.at(-1)).toMatchObject({ fromObjectId: input, toObjectId: monitor });
    await store.connect({ source: { nodeId: monitor, portId: 'out0' }, target: { nodeId: sequencer, portId: 'in0' } });
    const before = server.requests.length;
    const loop = { source: { nodeId: monitor, portId: 'out0' }, target: { nodeId: monitor, portId: 'in0' } };
    expect(store.connectionProblem(loop)).toMatch(/loop/);
    expect(await store.connect(loop)).toBe(false);
    expect(server.requests.length).toBe(before);
    expect(
      store.connectionProblem({
        source: { nodeId: input, portId: 'out0' },
        target: { nodeId: monitor, portId: 'out0' }
      })
    ).toMatch(/outlet to an inlet/);
  });

  it('moves, deletes and disconnects through the selection; layers are canvases', async () => {
    const { store } = await setUp();
    await store.ensureDefault();
    const [input] = store.environment().objects;
    await store.move({
      source: 'node',
      nodes: [{ nodeId: input?.id ?? '', position: { x: 101.4, y: 77 }, group: undefined }]
    });
    expect(store.environment().objects[0]).toMatchObject({ x: 101, y: 77 });
    store.selection.set({ kind: 'cable', id: store.environment().cables[0]?.id ?? '' });
    await store.deleteSelection();
    expect(store.environment().cables).toEqual([]);
    await store.addLayer();
    expect(store.layers()).toHaveLength(2);
    expect(store.objects()).toEqual([]);
    await store.addObject('keyboard');
    expect(store.objects().map(o => o.kind)).toEqual(['keyboard']);
    await store.renameLayer(store.layerId() ?? '', 'Pads');
    expect(store.layers()[1]?.name).toBe('Pads');
    store.selection.set({ kind: 'object', id: store.objects()[0]?.id ?? '' });
    await store.deleteSelection();
    expect(store.objects()).toEqual([]);
  });

  it('plays a keyboard and a fader into the graph; monitors and faders show what is reported', async () => {
    const { store } = await setUp();
    await store.ensureDefault();
    await store.addObject('keyboard');
    const keyboard = store.selectedObject();
    await store.addObject('monitor');
    const monitor = store.selectedObject();
    await store.addObject('fader');
    const fader = store.selectedObject();
    if (!keyboard || !monitor || !fader) {
      throw new Error('objects missing');
    }
    await store.connect({
      source: { nodeId: keyboard.id, portId: 'out0' },
      target: { nodeId: monitor.id, portId: 'in0' }
    });
    await store.play(keyboard.id, [{ status: 0x90, data1: 64, data2: 100 }]);
    await settle();
    expect(store.monitors().get(monitor.id)).toEqual([{ status: 0x90, data1: 64, data2: 100, sample: -1 }]);
    await store.moveFader(fader, 90);
    await settle();
    expect(store.faderValue(fader)).toBe(90);
  });

  it('opens as a window on the default pair', async () => {
    await setUp();
    TestBed.inject(UiStore).environmentOpen.set(true);
    const fixture = TestBed.createComponent(EnvironmentWindow);
    await fixture.whenStable();
    await settle();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="environment-node-Physical Input"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="environment-node-Sequencer Input"]')).not.toBeNull();
    (element.querySelector('[data-testid="environment-palette-monitor"]') as HTMLButtonElement).click();
    await settle();
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="environment-node-Monitor"]')).not.toBeNull();
    expect(element.querySelector<HTMLInputElement>('[data-testid="environment-name"]')?.value).toBe('Monitor');
    expect(
      element.querySelector<HTMLButtonElement>('[data-testid="environment-palette-sequencerInput"]')?.disabled
    ).toBe(true);
  });
});
