import { TestBed } from '@angular/core/testing';
import type { MidiRegion } from '@anode/api-client';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { QuantizeGrids } from '../midi/midi-math';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { TrackHeader } from '../tracks/track-header';
import { grooveChoices, quantizeChoices, regionQuantizeCommand } from './groove-menu';
import { grooveDetails, GrooveWindow } from './groove-window';

async function openProject() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Grooves' }));
  await project.execute(
    [
      { type: 'addTrack', kind: 'instrument', stereo: false },
      { type: 'addTrack', kind: 'instrument', stereo: false }
    ],
    'Tracks'
  );
  const [drums = '', keys = ''] = project.trackIds();
  await project.execute([{ type: 'addMidiRegion', trackId: drums, startTick: 0, lengthTicks: 3840 }], 'Region');
  const regionId = [...project.midiRegions().keys()][0] ?? '';
  // A swung 1/16 beat: every second step late by 60 ticks.
  await project.execute(
    [
      {
        type: 'addNotes',
        regionId,
        notes: Array.from({ length: 16 }, (_, i) => ({
          pitch: 42,
          velocity: i % 2 ? 64 : 127,
          channel: 1,
          startTick: i * 240 + (i % 2 ? 60 : 0),
          lengthTicks: 60
        }))
      }
    ],
    'Beat'
  );
  return { server, project, drums, keys, regionId };
}

describe('Groove menus', () => {
  it('lists the grooves below the grid values', () => {
    const choices = quantizeChoices(QuantizeGrids, [{ id: 'grv_1', name: 'Swing' }]);
    expect(choices.slice(-1)).toEqual([{ ticks: 0, label: 'Groove: Swing', grooveId: 'grv_1' }]);
    expect(choices.slice(0, -1).every(c => c.grooveId === undefined)).toBe(true);
    expect(grooveChoices([])).toEqual([]);
  });

  it('turns the region inspector’s choice into setMidiRegion: a groove, or back to the grid', () => {
    const region = (grooveId: string | null, quantizeTicks = 240) =>
      ({
        id: 'mid_1',
        playback: { transpose: 0, velocity: 0, quantizeTicks, quantizeStrength: 1, swing: 0, grooveId }
      }) as Pick<MidiRegion, 'id' | 'playback'>;
    const groove = { ticks: 0, label: 'Groove: Swing', grooveId: 'grv_1' };
    expect(regionQuantizeCommand(region(null), groove)).toEqual({
      type: 'setMidiRegion',
      regionId: 'mid_1',
      grooveId: 'grv_1'
    });
    expect(regionQuantizeCommand(region('grv_1'), groove)).toBeNull();
    expect(regionQuantizeCommand(region('grv_1'), { ticks: 240, label: '1/16' })).toEqual({
      type: 'setMidiRegion',
      regionId: 'mid_1',
      quantizeTicks: 240,
      grooveId: ''
    });
    expect(regionQuantizeCommand(region(null), { ticks: 240, label: '1/16' })).toBeNull();
    expect(regionQuantizeCommand(region(null), { ticks: 480, label: '1/8' })).toEqual({
      type: 'setMidiRegion',
      regionId: 'mid_1',
      quantizeTicks: 480
    });
  });

  it('describes a template', () => {
    expect(
      grooveDetails({
        lengthTicks: 3840,
        stepTicks: 240,
        points: Array.from({ length: 16 }, (_, i) => ({ tick: i * 240, velocityScale: 1 }))
      })
    ).toBe('16 steps of 1/16');
  });
});

describe('GrooveWindow', () => {
  it('makes a template from a region, saves it to the library and adds it back', async () => {
    const { server, project, regionId } = await openProject();
    TestBed.inject(UiStore).grooves.set({ regionId });
    const fixture = TestBed.createComponent(GrooveWindow);
    const update = async () => {
      await settle();
      await fixture.whenStable();
    };
    await update();
    const element = fixture.nativeElement as HTMLElement;
    const byTestId = (id: string) => element.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    expect(byTestId('groove-source')?.textContent).toContain('(notes)');

    const name = byTestId('groove-name') as HTMLInputElement | null;
    if (name) {
      name.value = 'Swing 16';
      name.dispatchEvent(new Event('input'));
    }
    const bars = byTestId('groove-bars') as HTMLSelectElement | null;
    if (bars) {
      bars.value = '1';
      bars.dispatchEvent(new Event('change'));
    }
    const grid = byTestId('groove-grid') as HTMLSelectElement | null;
    if (grid) {
      grid.value = '240';
      grid.dispatchEvent(new Event('change'));
    }
    (byTestId('groove-make') as HTMLButtonElement | null)?.click();
    await update();
    const groove = project.grooveList()[0];
    expect(groove?.name).toBe('Swing 16');
    expect(groove?.stepTicks).toBe(240);
    expect(groove?.points.slice(0, 4).map(p => p.tick)).toEqual([0, 300, 480, 780]);
    expect(byTestId('groove-status')?.textContent).toContain('Made “Swing 16”.');

    (byTestId('groove-save-library') as HTMLButtonElement | null)?.click();
    await update();
    expect((await server.libraryGrooves()).map(g => g.name)).toEqual(['Swing 16']);
    expect(element.querySelectorAll('[data-testid="library-groove"]')).toHaveLength(1);

    (byTestId('groove-delete') as HTMLButtonElement | null)?.click();
    await update();
    expect(project.grooves().size).toBe(0);
    (byTestId('groove-add-library') as HTMLButtonElement | null)?.click();
    await update();
    expect(project.grooveList().map(g => g.name)).toEqual(['Swing 16']);
  });

  it('quantises a straight region to the groove (quantizeNotes with grooveId)', async () => {
    const { project, regionId, keys } = await openProject();
    await project.execute([{ type: 'makeGroove', regionId, name: 'Swing', lengthBars: 1, stepTicks: 240 }], 'Make');
    const grooveId = project.grooveList()[0]?.id ?? '';
    await project.execute([{ type: 'addMidiRegion', trackId: keys, startTick: 0, lengthTicks: 3840 }], 'Region');
    const straight = [...project.midiRegions().values()].find(r => r.trackId === keys)?.id ?? '';
    await project.execute(
      [
        {
          type: 'addNotes',
          regionId: straight,
          notes: [0, 240, 480].map(startTick => ({ pitch: 60, velocity: 100, channel: 1, startTick, lengthTicks: 120 }))
        }
      ],
      'Notes'
    );
    await project.execute(
      [{ type: 'quantizeNotes', regionId: straight, gridTicks: 240, strength: 1, swing: 0, grooveId }],
      'Quantize'
    );
    expect(
      project
        .midiRegions()
        .get(straight)
        ?.notes.map(n => n.startTick)
    ).toEqual([0, 300, 480]);
  });
});

describe('Groove track toggles', () => {
  it('makes a track the groove track and another match it', async () => {
    const { project, drums, keys } = await openProject();
    const header = (trackId: string, index: number) => {
      const fixture = TestBed.createComponent(TrackHeader);
      fixture.componentRef.setInput('track', project.track(trackId));
      fixture.componentRef.setInput('index', index);
      return fixture;
    };
    const drumHeader = header(drums, 0);
    const keysHeader = header(keys, 1);
    await drumHeader.whenStable();
    await keysHeader.whenStable();
    const star = (drumHeader.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[data-testid="groove-track"]'
    );
    expect(star?.getAttribute('aria-label')).toBe('Groove track');
    expect(star?.getAttribute('aria-pressed')).toBe('false');
    expect((keysHeader.nativeElement as HTMLElement).querySelector('[data-testid="match-groove"]')).toBeNull();

    star?.click();
    await settle();
    expect(project.settings().grooveTrackId).toBe(drums);
    await drumHeader.whenStable();
    expect(star?.getAttribute('aria-pressed')).toBe('true');
    await keysHeader.whenStable();
    const match = (keysHeader.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '[data-testid="match-groove"]'
    );
    expect(match?.getAttribute('aria-label')).toBe('Match groove track');
    expect((drumHeader.nativeElement as HTMLElement).querySelector('[data-testid="match-groove"]')).toBeNull();
    if (match) {
      match.checked = true;
      match.dispatchEvent(new Event('change'));
    }
    await settle();
    expect(project.track(keys)?.matchGroove).toBe(true);
  });
});
