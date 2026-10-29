import { TestBed } from '@angular/core/testing';
import type { MidiRegion } from '@anode/api-client';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { DefaultLaneSet } from '../midi-edit/lanes';
import { PianoRollStore } from '../state/piano-roll.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { StepEditor } from './step-editor';
import { yAtValue } from './step-gestures';

describe('Step Editor', () => {
  async function open() {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Steps' }));
    await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: false }], 'Track');
    const trackId = project.trackIds()[0] ?? '';
    await project.execute([{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 }], 'Region');
    const regionId = [...project.midiRegions().keys()][0] ?? '';
    await project.execute(
      [
        {
          type: 'addNotes',
          regionId,
          notes: [0, 960, 1920, 2880].map(startTick => ({
            pitch: 60,
            velocity: 50,
            channel: 1,
            startTick,
            lengthTicks: 240
          }))
        }
      ],
      'Notes'
    );
    TestBed.inject(SelectionStore).selectTrack(trackId);
    const fixture = TestBed.createComponent(StepEditor);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const region = () => project.midiRegions().get(regionId) as MidiRegion;
    const byTestId = (id: string) => {
      const found = element.querySelector<HTMLElement>(`[data-testid="${id}"]`);
      if (!found) {
        throw new Error(`No ${id}`);
      }
      return found;
    };
    const stable = async () => {
      await settle();
      await fixture.whenStable();
    };
    /** A pointer drag on a lane's body from one point to another (body pixels; the test DOM's rects are at 0). */
    const drag = async (lane: HTMLElement, from: { x: number; y: number }, to: { x: number; y: number }) => {
      const event = (type: string, p: { x: number; y: number }) =>
        new MouseEvent(type, { bubbles: true, button: 0, clientX: p.x, clientY: p.y });
      lane.dispatchEvent(event('pointerdown', from));
      lane.dispatchEvent(event('pointermove', { x: (from.x + to.x) / 2, y: (from.y + to.y) / 2 }));
      lane.dispatchEvent(event('pointerup', to));
      await stable();
    };
    return { project, fixture, element, region, byTestId, stable, drag };
  }

  it('shows the default lane set until a change stores it, and adds a CC 74 lane', async () => {
    const { project, element, byTestId, fixture, stable } = await open();
    const lanes = () =>
      [...element.querySelectorAll('an-step-lane [role="group"]')].map(l => l.getAttribute('aria-label'));
    expect(lanes()).toEqual(DefaultLaneSet.lanes.map(l => `${l.name} lane`));
    expect(byTestId('step-lane-velocity').getAttribute('role')).toBe('group');
    expect(byTestId('step-lane-controlChange-1')).toBeTruthy();
    expect(byTestId('lane-set-menu').textContent).toContain('Default');
    expect(project.laneSetList()).toEqual([]);

    const number = byTestId('add-lane-number') as HTMLInputElement;
    number.value = '74';
    number.dispatchEvent(new Event('input'));
    byTestId('add-lane').click();
    await stable();
    const item = document.querySelector<HTMLElement>('[data-testid="add-lane-controlChange"]');
    expect(item?.textContent.trim()).toBe('Control Change 74');
    item?.click();
    await stable();
    expect(project.laneSetList()).toHaveLength(1);
    const stored = project.laneSetList()[0];
    expect(stored?.id).toMatch(/^lns_/);
    expect(stored?.lanes.at(-1)).toEqual({ kind: 'controlChange', number: 74, name: 'CC 74', resolutionTicks: 240 });
    expect(fixture.componentInstance.set().id).toBe(stored?.id);
    expect(byTestId('step-lane-controlChange-74').getAttribute('aria-label')).toBe('CC 74 lane');

    // Resolution and removal: edits of the stored set.
    await fixture.componentInstance.setResolution(0, 480);
    await stable();
    expect(project.laneSetList()[0]?.lanes[0]?.resolutionTicks).toBe(480);
    expect(byTestId('lane-resolution-0').textContent.trim()).toBe('1/8');
    byTestId('lane-remove-1').click();
    await stable();
    expect(project.laneSetList()[0]?.lanes.map(l => l.name)).toEqual([
      'Velocity',
      'Pitch Bend',
      'Channel Pressure',
      'Program',
      'CC 74'
    ]);
  });

  it('draws a velocity ramp and a CC 74 line over a bar, and paints with the pencil', async () => {
    const { project, region, byTestId, fixture, stable, drag } = await open();
    await fixture.componentInstance.addLane('controlChange');
    await stable();

    const line = byTestId('step-tool-line');
    expect(line.getAttribute('role')).toBe('radio');
    expect(line.getAttribute('aria-checked')).toBe('false');
    line.click();
    await stable();
    expect(line.getAttribute('aria-checked')).toBe('true');

    // The Piano Roll's scale: 96 px a quarter (0.1 px a tick) from tick 0, so a 4/4 bar is 384 px.
    const store = TestBed.inject(PianoRollStore);
    expect(store.pixelsPerTick()).toBe(0.1);
    const revision = project.revision();
    await drag(
      byTestId('step-lane-velocity'),
      { x: 0, y: yAtValue(1, 64, [1, 127]) },
      { x: 384, y: yAtValue(127, 64, [1, 127]) }
    );
    expect(project.revision()).toBe(revision + 1);
    expect(region().notes.map(n => n.velocity)).toEqual([1, 33, 64, 96]);

    await drag(
      byTestId('step-lane-controlChange-74'),
      { x: 0, y: yAtValue(0, 64, [0, 127]) },
      { x: 384, y: yAtValue(127, 64, [0, 127]) }
    );
    const cc = region().controls.filter(c => c.kind === 'controlChange' && c.number === 74);
    expect(cc.map(c => c.tick)).toEqual(Array.from({ length: 16 }, (_, i) => i * 240));
    expect(cc.map(c => c.value)).toEqual(Array.from({ length: 16 }, (_, i) => Math.round((127 * i) / 16)));
    expect(byTestId('step-lane-controlChange-74').querySelectorAll('[role="slider"]')).toHaveLength(16);

    // The pencil replaces the value of the step it touches.
    byTestId('step-tool-pencil').click();
    await stable();
    await drag(
      byTestId('step-lane-controlChange-74'),
      { x: 50, y: yAtValue(100, 64, [0, 127]) },
      { x: 50, y: yAtValue(100, 64, [0, 127]) }
    );
    const after = region().controls.filter(c => c.kind === 'controlChange' && c.number === 74);
    expect(after).toHaveLength(16);
    expect(after.find(c => c.tick === 480)?.value).toBe(100);

    // A bar's value from the keyboard.
    const bar = byTestId('step-lane-controlChange-74').querySelector<HTMLElement>('[role="slider"]');
    expect(bar?.getAttribute('aria-valuenow')).toBe('0');
    expect(bar?.getAttribute('tabindex')).toBe('0');
    bar?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }));
    await stable();
    expect(region().controls.find(c => c.number === 74 && c.tick === 0)?.value).toBe(1);
  });

  it('makes, renames, chooses and deletes lane sets', async () => {
    const { project, byTestId, stable, fixture } = await open();
    byTestId('lane-set-menu').click();
    await stable();
    document.querySelector<HTMLElement>('[data-testid="lane-set-new"]')?.click();
    await stable();
    expect(project.laneSetList().map(s => s.name)).toEqual(['Lane Set 1']);
    expect(project.laneSetList()[0]?.lanes).toEqual(DefaultLaneSet.lanes);

    fixture.componentInstance.renaming.set(true);
    await stable();
    const name = byTestId('lane-set-name') as HTMLInputElement;
    name.value = 'Strings';
    name.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await stable();
    expect(project.laneSetList().map(s => s.name)).toEqual(['Strings']);
    expect(byTestId('lane-set-menu').textContent).toContain('Strings');

    await fixture.componentInstance.deleteSet();
    await stable();
    expect(project.laneSetList()).toEqual([]);
    expect(fixture.componentInstance.set()).toBe(DefaultLaneSet);
  });
});
