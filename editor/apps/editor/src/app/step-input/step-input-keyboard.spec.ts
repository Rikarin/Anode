import { TestBed } from '@angular/core/testing';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { stepTarget } from '../midi-edit/step-input';
import { PianoRoll } from '../piano-roll/piano-roll';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { StepInputStore } from '../state/step-input.store';
import { UiStore } from '../state/ui.store';
import { StepInputKeyboard } from './step-input-keyboard';

async function setUp() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Steps' }));
  await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: false }], 'Track');
  const trackId = project.trackIds()[0] ?? '';
  const selection = TestBed.inject(SelectionStore);
  selection.selectTrack(trackId);
  const steps = TestBed.inject(StepInputStore);
  const region = () => [...project.midiRegions().values()].find(r => r.id === steps.state().regionId);
  const notes = () =>
    [...(region()?.notes ?? [])]
      .sort((a, b) => a.startTick - b.startTick || a.pitch - b.pitch)
      .map(n => [n.pitch, n.startTick, n.lengthTicks, n.velocity]);
  return { server, project, trackId, selection, steps, region, notes };
}

async function openKeyboard() {
  TestBed.inject(UiStore).stepInputKeyboardOpen.set(true);
  const fixture = TestBed.createComponent(StepInputKeyboard);
  fixture.autoDetectChanges();
  await settle();
  await settle();
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;
  const byTestId = (id: string) => {
    const found = element.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    if (!found) {
      throw new Error(`No ${id}`);
    }
    return found;
  };
  const key = (name: string) => {
    const found = [...element.querySelectorAll<HTMLElement>('[data-testid="step-keys"] button')].find(
      k => k.getAttribute('aria-label') === name
    );
    if (!found) {
      throw new Error(`No key ${name}`);
    }
    return found;
  };
  const press = (name: string) => key(name).dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true }));
  const release = (name: string) => key(name).dispatchEvent(new KeyboardEvent('keyup', { key: ' ', bubbles: true }));
  const play = async (...names: string[]) => {
    names.forEach(press);
    names.forEach(release);
    await settle();
    await fixture.whenStable();
  };
  const click = async (id: string) => {
    byTestId(id).click();
    await settle();
    await fixture.whenStable();
  };
  return { fixture, element, byTestId, key, play, click };
}

describe('Step input', () => {
  it('finds where steps go: the selected MIDI region, else the selected instrument or MIDI track', async () => {
    const { project, trackId } = await setUp();
    await project.execute(
      [
        { type: 'addMidiRegion', trackId, startTick: 3840, lengthTicks: 3840 },
        { type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 }
      ],
      'Regions'
    );
    const regions = project.midiRegions();
    const ids = [...regions.keys()];
    const first = [...regions.values()].find(r => r.startTick === 0)?.id;
    expect(stepTarget(regions, project.tracks(), ids, null)).toEqual({ regionId: first, trackId });
    expect(stepTarget(regions, project.tracks(), [], trackId)).toEqual({ regionId: null, trackId });
    expect(stepTarget(regions, project.tracks(), [], null)).toBeNull();
    await project.execute([{ type: 'addTrack', kind: 'audio', stereo: false }], 'Audio');
    const audio = project.trackIds().find(id => project.track(id)?.kind === 'audio') ?? '';
    expect(stepTarget(regions, project.tracks(), [], audio)).toBeNull();
  });

  it('enters notes, a chord, a rest and a tie from the Step Input Keyboard into a new region at the playhead', async () => {
    const { steps, notes, trackId } = await setUp();
    const w = await openKeyboard();
    expect(steps.state()).toMatchObject({ on: true, trackId, regionId: null, positionTick: 0 });
    expect(w.byTestId('step-position').textContent.trim()).toBe('1 1 1 1');
    expect(w.byTestId('step-target').textContent).toContain('New region on');

    // Quarter notes at mf.
    await w.click('step-value-960');
    expect(w.byTestId('step-value-960').getAttribute('aria-checked')).toBe('true');
    await w.click('step-velocity-mf');
    expect(steps.state()).toMatchObject({ noteValueTicks: 960, velocity: 80 });

    await w.play('C3');
    await w.play('D3');
    // Two keys down together: one chord.
    await w.play('E3', 'G3');
    expect(w.byTestId('step-position').textContent.trim()).toBe('1 4 1 1');
    await w.click('step-rest');
    // Chord mode: keys stack at one position until it is turned off.
    await w.click('step-chord');
    expect(w.byTestId('step-chord').getAttribute('aria-pressed')).toBe('true');
    await w.play('C3');
    await w.play('F3');
    await w.click('step-chord');
    // A tie lengthens the last step.
    await w.click('step-value-480');
    await w.play('A3');
    await w.click('step-tie');
    expect(notes()).toEqual([
      [60, 0, 960, 80],
      [62, 960, 960, 80],
      [64, 1920, 960, 80],
      [67, 1920, 960, 80],
      [60, 3840, 960, 80],
      [65, 3840, 960, 80],
      [69, 4800, 960, 80]
    ]);
    expect(steps.state().positionTick).toBe(5760);
    expect(w.byTestId('step-position').textContent.trim()).toBe('2 3 1 1');

    // Dotted and triplet change the step; the velocity field takes a value.
    await w.click('step-dotted');
    expect(steps.state().dotted).toBe(true);
    await w.click('step-triplet');
    expect(steps.state().triplet).toBe(true);
    const velocity = w.byTestId('step-velocity') as HTMLInputElement;
    velocity.value = '33';
    velocity.dispatchEvent(new Event('change'));
    await settle();
    expect(steps.state().velocity).toBe(33);

    // Closing the window turns step input off.
    w.fixture.destroy();
    await settle();
    expect(steps.state().on).toBe(false);
  });

  it('enters steps into the selected region, and stays disabled with nothing selected', async () => {
    const { project, selection, steps, trackId } = await setUp();
    await project.execute([{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 }], 'Region');
    const regionId = [...project.midiRegions().keys()][0] ?? '';
    selection.select([regionId]);
    const w = await openKeyboard();
    expect(steps.state()).toMatchObject({ on: true, regionId });
    expect(w.byTestId('step-target').textContent).toContain('Into');
    await w.play('C3');
    expect(
      project
        .midiRegions()
        .get(regionId)
        ?.notes.map(n => n.pitch)
    ).toEqual([60]);
    w.fixture.destroy();
    await settle();

    selection.clear();
    selection.track.set(null);
    const empty = await openKeyboard();
    expect(steps.state().on).toBe(false);
    expect((empty.byTestId('step-rest') as HTMLButtonElement).disabled).toBe(true);
  });

  it('turns MIDI step input on from the Piano Roll, shows the step position and steps from MIDI input', async () => {
    const { server, project, selection, steps, trackId, notes } = await setUp();
    await project.execute([{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 }], 'Region');
    const regionId = [...project.midiRegions().keys()][0] ?? '';
    selection.select([regionId]);
    const fixture = TestBed.createComponent(PianoRoll);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const toggle = element.querySelector<HTMLButtonElement>('[data-testid="midi-step-input"]');
    expect(toggle?.getAttribute('aria-label')).toBe('MIDI Step Input');
    expect(toggle?.getAttribute('aria-pressed')).toBe('false');
    expect(element.querySelector('[data-testid="step-input-marker"]')).toBeNull();
    toggle?.click();
    await settle();
    await fixture.whenStable();
    expect(steps.state()).toMatchObject({ on: true, midiInput: true, regionId, positionTick: 0 });
    expect(toggle?.getAttribute('aria-pressed')).toBe('true');
    const marker = () => element.querySelector<HTMLElement>('[data-testid="step-input-marker"]');
    expect(marker()?.getAttribute('aria-label')).toBe('Step input position 1 1 1 1');

    // The host forwards held keys as one step when the first is released.
    await server.sendLiveMidi(trackId, [
      { status: 0x90, data1: 60, data2: 90 },
      { status: 0x90, data1: 64, data2: 100 },
      { status: 0x80, data1: 60, data2: 0 },
      { status: 0x80, data1: 64, data2: 0 },
      { status: 0x90, data1: 67, data2: 70 },
      { status: 0x80, data1: 67, data2: 0 }
    ]);
    await settle();
    await fixture.whenStable();
    expect(notes()).toEqual([
      [60, 0, 240, 100],
      [64, 0, 240, 100],
      [67, 240, 240, 70]
    ]);
    expect(marker()?.dataset['tick']).toBe('480');
    expect(marker()?.getAttribute('aria-label')).toBe('Step input position 1 1 3 1');

    toggle?.click();
    await settle();
    await fixture.whenStable();
    expect(steps.state()).toMatchObject({ on: false, midiInput: false });
    expect(marker()).toBeNull();
  });
});
