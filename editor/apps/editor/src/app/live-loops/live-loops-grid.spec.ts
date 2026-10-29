import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { TracksViewStore } from '../state/tracks-view.store';
import { LoopDragType } from './grid-model';
import { LiveLoopsGrid } from './live-loops-grid';
import { LiveLoopsStore } from './live-loops.store';

async function render() {
  const clock = { time: 0 };
  const server = setUpFakeServer({ now: () => clock.time });
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Grid' }));
  await project.execute(
    [
      { type: 'addTrack', kind: 'audio', stereo: true },
      { type: 'addTrack', kind: 'instrument', stereo: true }
    ],
    'Tracks'
  );
  const live = TestBed.inject(LiveLoopsStore);
  await live.addScene();
  await live.addScene();
  await server.installPack('/x/TestPack.anodepack');
  const fixture = TestBed.createComponent(LiveLoopsGrid);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;
  const [audio = '', synth = ''] = project.trackIds();
  const [s1 = '', s2 = ''] = project.liveLoops().scenes.map(s => s.id);
  const slot = (trackId: string, sceneId: string) =>
    element.querySelector<HTMLElement>(
      `[data-testid="live-loops-cell"][data-track="${trackId}"][data-scene="${sceneId}"]`
    );
  return { server, project, live, clock, fixture, element, audio, synth, s1, s2, slot };
}

/** A drop event carrying a loop id, as the Loop Browser's drag does. */
function loopDrop(type: 'dragover' | 'drop', loopId: string): Event {
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'dataTransfer', {
    value: { types: [LoopDragType], getData: () => loopId, dropEffect: 'none' }
  });
  return event;
}

describe('LiveLoopsGrid', () => {
  it('lays out a row per track beside the lanes, and a named, empty cell per scene', async () => {
    const { project, element, audio, synth, fixture } = await render();
    const rows = [...element.querySelectorAll<HTMLElement>('[role="grid"] [role="row"]')];
    // The header row, then the tracks in the Tracks area's order, each a lane high.
    expect(rows.map(r => r.dataset['track'] ?? 'header')).toEqual(['header', audio, synth]);
    const view = TestBed.inject(TracksViewStore);
    expect(rows[1]?.style.height).toBe(`${view.laneHeight()}px`);
    expect(element.querySelectorAll('[data-testid="scene-launch"]')).toHaveLength(2);
    const name = project.track(audio)?.name ?? '';
    const cell = element.querySelector('[role="gridcell"]');
    expect(cell?.getAttribute('aria-label')).toBe(`Empty cell, ${name}, scene 1`);
    expect(cell?.getAttribute('data-state')).toBe('empty');
    // The rows scroll with the Tracks area.
    view.firstLane.set(1);
    fixture.detectChanges();
    expect(rows[1]?.parentElement?.style.transform).toBe(`translateY(${-view.laneHeight()}px)`);
  });

  it('takes a dropped loop, launches a clicked cell at the boundary and shows it playing', async () => {
    const { server, project, clock, fixture, element, synth, s1, slot } = await render();
    const target = slot(synth, s1);
    const over = loopDrop('dragover', 'test-pack:bass-line');
    target?.dispatchEvent(over);
    expect(over.defaultPrevented).toBe(true);
    target?.dispatchEvent(loopDrop('drop', 'test-pack:bass-line'));
    await settle();
    await fixture.whenStable();
    expect(project.liveLoops().cells).toHaveLength(1);
    const name = project.track(synth)?.name ?? '';
    expect(slot(synth, s1)?.getAttribute('aria-label')).toBe(`Bass Line, ${name}, scene 1`);
    expect(slot(synth, s1)?.getAttribute('data-state')).toBe('stopped');

    slot(synth, s1)?.querySelector('button')?.click();
    await settle();
    await fixture.whenStable();
    expect(slot(synth, s1)?.getAttribute('data-state')).toBe('queued');
    clock.time = 2_100;
    server.advance();
    await settle();
    await fixture.whenStable();
    expect(slot(synth, s1)?.getAttribute('data-state')).toBe('playing');
    expect(slot(synth, s1)?.getAttribute('aria-selected')).toBe('true');
    // The selected cell opens the cell editor.
    expect(element.querySelector('[data-testid="cell-editor"]')).not.toBeNull();
  });

  it('switches track activation, the grid quantise and performance recording from its controls', async () => {
    const { server, project, live, element, fixture, audio } = await render();
    const activation = element.querySelector<HTMLButtonElement>('[data-testid="track-activation"]');
    expect(activation?.getAttribute('aria-pressed')).toBe('false');
    activation?.click();
    await settle();
    await fixture.whenStable();
    expect(project.track(audio)?.liveLoopsActive).toBe(true);
    expect(activation?.getAttribute('aria-pressed')).toBe('true');

    await live.setQuantize('fourBars');
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="live-loops-quantize"]')?.textContent).toContain('4 Bars');

    const record = element.querySelector<HTMLButtonElement>('[data-testid="live-loops-record"]');
    record?.click();
    await settle();
    await fixture.whenStable();
    expect(server.liveRecording).toBe('performance');
    expect(record?.getAttribute('aria-pressed')).toBe('true');
    expect(element.getAttribute('data-recording')).toBe('true');

    element.querySelector<HTMLButtonElement>('[data-testid="remix-fx-toggle"]')?.click();
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="remix-fx"]')).not.toBeNull();

    element.querySelector<HTMLButtonElement>('[data-testid="add-scene"]')?.click();
    await settle();
    await fixture.whenStable();
    expect(element.querySelectorAll('[data-testid="scene-launch"]')).toHaveLength(3);
  });
});
