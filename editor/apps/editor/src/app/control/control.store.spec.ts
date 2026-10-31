import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { KeyCommandsStore } from '../state/key-commands.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { ControlStore } from './control.store';
import { SelectionDebounceMs, SelectionReporter } from './selection-reporter';

async function setUp() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Control' }));
  await project.execute(
    [
      { type: 'addTrack', stereo: true, name: 'Drums' },
      { type: 'addTrack', stereo: true, name: 'Bass' }
    ],
    'Tracks'
  );
  const [drums, bass] = project.trackList().map(t => t.id);
  const control = TestBed.inject(ControlStore);
  await control.load();
  return { server, project, control, drums: drums ?? '', bass: bass ?? '' };
}

describe('ControlStore', () => {
  it('learns: the banner’s last message, a clicked target, the assignment made and selected', async () => {
    const { server, control, bass } = await setUp();
    await control.startLearn(null);
    expect(control.learning()).toBe(true);
    server.controlMidi('anode:in', 0xb0, 21, 40);
    await settle();
    expect(control.learnState().last).toEqual(expect.objectContaining({ data1: 21, data2: 40 }));
    expect(control.assignments().zones).toEqual([]);

    await control.setLearnTarget({ kind: 'parameter', trackId: bass, address: 'volume' });
    expect(control.learnState().target).toEqual({ kind: 'parameter', trackId: bass, address: 'volume' });
    server.controlMidi('anode:in', 0xb0, 21, 41);
    await settle();
    const id = control.selectedId();
    expect(id).toBeTruthy();
    expect(control.learnedId()).toBe(id);
    expect(control.selected()?.assignment.input).toEqual({
      port: 'anode:in',
      kind: 'controlChange',
      channel: 1,
      number: 21
    });
    expect(control.targetLabel(control.selected()?.assignment.target)).toBe('Bass: Volume');

    // A button for the selected track's mute, in the same mode.
    await control.setLearnTarget({ kind: 'trackSwitch', trackId: null, switch: 'mute' });
    server.controlMidi('anode:in', 0x90, 36, 127);
    await settle();
    expect(control.targetLabel(control.selected()?.assignment.target)).toBe('Selected Track: Mute');
    expect(control.assignments().zones[0]?.modes[0]?.assignments).toHaveLength(2);
    await control.stopLearn();
    expect(control.learning()).toBe(false);
  });

  it('“Learn Assignment for …” opens the window in Learn with the target', async () => {
    const { control, drums } = await setUp();
    await control.learnFor({ kind: 'parameter', trackId: drums, address: 'pan' });
    expect(TestBed.inject(UiStore).controllerAssignmentsOpen()).toBe(true);
    expect(control.learnState()).toEqual(
      expect.objectContaining({ on: true, target: { kind: 'parameter', trackId: drums, address: 'pan' } })
    );
  });

  it('edits assignments, zones and modes, and takes back what the Server refuses', async () => {
    const { server, control } = await setUp();
    await control.addAssignment();
    const id = control.selectedId() ?? '';
    expect(control.selected()?.assignment.name).toBe('Assignment 1');
    expect((await server.controllerAssignments()).zones[0]?.modes[0]?.assignments.map(a => a.id)).toEqual([id]);

    expect(await control.updateAssignment(id, { input: { port: null, kind: 'note', channel: 3, number: 60 } })).toBe(
      true
    );
    expect(control.selected()?.assignment.input.kind).toBe('note');
    // Out of range: refused and the shown value goes back.
    expect(await control.updateAssignment(id, { max: 2 })).toBe(false);
    expect(control.selected()?.assignment.max).toBe(1);

    const zoneId = control.assignments().zones[0]?.id ?? '';
    await control.addMode(zoneId);
    const modes = control.assignments().zones[0]?.modes ?? [];
    expect(modes.map(m => m.name)).toEqual(['Mode 1', 'Mode 2']);
    await control.setActiveMode(zoneId, modes[1]?.id ?? '');
    expect(control.assignments().zones[0]?.activeModeId).toBe(modes[1]?.id);
    await control.moveAssignment(id, modes[1]?.id ?? '');
    expect(control.selected()?.mode.id).toBe(modes[1]?.id);
    await control.renameZone(zoneId, 'Keyboard');
    await control.addZone();
    expect(control.assignments().zones.map(z => z.name)).toEqual(['Keyboard', 'Zone 1']);
    await control.deleteAssignment(id);
    expect(control.selectedId()).toBeNull();
    expect(await server.controllerAssignments()).toEqual(control.assignments());
  });

  it('adds, orders and removes surfaces and follows their status', async () => {
    const { server, control } = await setUp();
    await control.addSurface('mackieControl', 'anode:in', 'anode:out');
    await control.addSurface('mackieControlXt', 'in:iac-1', 'out:iac-1');
    const [main, xt] = control.surfaces();
    expect(control.surfaceStatus().get(main?.id ?? '')?.connected).toBe(true);
    await control.moveSurface(xt?.id ?? '', -1);
    expect(control.surfaces().map(s => s.kind)).toEqual(['mackieControlXt', 'mackieControl']);
    server.controlState.setSurfaceStatus(main?.id ?? '', { connected: false, bankOffset: 8 });
    await settle();
    expect(control.surfaceStatus().get(main?.id ?? '')).toEqual({ id: main?.id, connected: false, bankOffset: 8 });
    await control.removeSurface(xt?.id ?? '');
    expect(control.surfaces().map(s => [s.kind, s.order])).toEqual([['mackieControl', 0]]);
  });

  it('turns OSC on with a port', async () => {
    const { control } = await setUp();
    await control.setOsc({ enabled: true, port: 9100, clients: [] });
    expect(control.osc()?.listening).toBe(true);
    expect(control.osc()?.settings.port).toBe(9100);
    expect(control.osc()?.addresses.length).toBeGreaterThan(0);
  });

  it('builds an OSC change on the one before it, even before the Server answered', async () => {
    const { server, control } = await setUp();
    // The port, then On at once (as the window does them): On keeps the new port.
    const settings = () => control.osc()?.settings ?? { enabled: false, port: 8000, clients: [] };
    const port = control.setOsc({ ...settings(), port: 9123 });
    const on = control.setOsc({ ...settings(), enabled: true });
    await Promise.all([port, on]);
    expect(control.osc()?.settings).toEqual(expect.objectContaining({ enabled: true, port: 9123 }));
    expect((await server.osc()).settings).toEqual(expect.objectContaining({ enabled: true, port: 9123 }));
  });
});

describe('SelectionReporter', () => {
  beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
  afterEach(() => vi.useRealTimers());

  it('reports the selected track once it rests, once per change', async () => {
    const { server, drums, bass } = await setUp();
    TestBed.inject(SelectionReporter);
    const selection = TestBed.inject(SelectionStore);
    const sent = () => server.requests.filter(r => r === 'SetSelection').length;
    const before = sent();

    selection.selectTrack(drums);
    TestBed.tick();
    selection.selectTrack(bass);
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(SelectionDebounceMs + 10);
    expect(server.controlState.selection).toBe(bass);
    expect(sent() - before).toBe(1);

    // The same track again sends nothing.
    selection.selectTrack(drums);
    TestBed.tick();
    selection.selectTrack(bass);
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(SelectionDebounceMs + 10);
    expect(sent() - before).toBe(1);

    selection.track.set(null);
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(SelectionDebounceMs + 10);
    expect(server.controlState.selection).toBeNull();
  });

  it('follows a selection made on hardware, without reporting it back', async () => {
    const { server, drums } = await setUp();
    TestBed.inject(SelectionReporter);
    const selection = TestBed.inject(SelectionStore);
    const sent = () => server.requests.filter(r => r === 'SetSelection').length;
    await vi.advanceTimersByTimeAsync(SelectionDebounceMs + 10);
    const before = sent();

    server.selectFromHardware(drums);
    await settle();
    expect(selection.track()).toBe(drums);
    expect([...selection.tracks()]).toEqual([drums]);
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(SelectionDebounceMs + 10);
    expect(sent()).toBe(before);

    server.selectFromHardware(null);
    await settle();
    expect(selection.track()).toBeNull();
  });
});

describe('Key commands from hardware', () => {
  it('runs a key command a controller asks for that the Server leaves to the editor', async () => {
    const { server, control } = await setUp();
    const keys = TestBed.inject(KeyCommandsStore);
    const run = vi.fn();
    keys.register('view.mixer', run);
    await control.save({
      zones: [
        {
          id: 'z',
          name: 'Zone',
          activeModeId: 'm',
          modes: [
            {
              id: 'm',
              name: 'Mode',
              assignments: [
                {
                  id: 'a',
                  name: null,
                  input: { port: null, kind: 'note', channel: null, number: 40 },
                  target: { kind: 'keyCommand', keyCommandId: 'view.mixer' },
                  mode: 'momentary',
                  min: 0,
                  max: 1,
                  pickup: false
                }
              ]
            }
          ]
        }
      ]
    });
    server.controlMidi('anode:in', 0x90, 40, 127);
    await settle();
    expect(run).toHaveBeenCalledTimes(1);
  });
});
