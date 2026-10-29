import { TestBed } from '@angular/core/testing';
import type { PatternRegion, Track } from '@anode/api-client';
import { ItemFlag } from '@anode/timeline';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { buildScene } from '../tracks/scene';
import { muteCommands, renameCommand } from '../tracks/tool-edits';
import { parseNote, StepSequencer } from './step-sequencer';

describe('Step Sequencer', () => {
  async function open() {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Beats' }));
    await server.installPack('/Users/anode/Downloads/Essentials.anodepack');
    await project.execute([{ type: 'createDrumMachine', kitPatchId: 'kit_electro', name: 'Beat' }], 'DMD');
    const dmd = project.trackList().find(t => t.drumMachine)?.id ?? '';
    const fixture = TestBed.createComponent(StepSequencer);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const byLabel = (label: string) => {
      const found = element.querySelector<HTMLElement>(`[aria-label="${label}"]`);
      if (!found) {
        throw new Error(`No ${label}`);
      }
      return found;
    };
    return { server, project, dmd, fixture, element, byLabel };
  }

  it('makes a pattern region on the selected DMD and edits its steps, values and rows', async () => {
    const { project, dmd, fixture, element, byLabel } = await open();
    const create = element.querySelector<HTMLButtonElement>('[data-testid="new-pattern-region"]');
    expect(create?.disabled).toBe(true);
    TestBed.inject(SelectionStore).selectTrack(dmd);
    await fixture.whenStable();
    create?.click();
    await settle();
    await fixture.whenStable();
    const rows = [...element.querySelectorAll('[role="row"]')].map(r => r.getAttribute('aria-label'));
    expect(rows).toEqual(['Kick', 'Snare', 'Clap', 'Closed Hat', 'Open Hat']);
    const region = () => project.patternRegionList()[0] as PatternRegion;

    byLabel('Kick step 1').click();
    byLabel('Kick step 5').click();
    await settle();
    await fixture.whenStable();
    expect(byLabel('Kick step 1').getAttribute('aria-pressed')).toBe('true');
    expect(region().pattern.rows[0]?.steps[4]?.on).toBe(true);

    // Value modes: a slider per step, ↓ lowers it a step.
    const modes = element.querySelector('[data-testid="step-edit-mode"]');
    const mode = (name: string) =>
      [...(modes?.querySelectorAll<HTMLElement>('[role="radio"]') ?? [])].find(r => r.textContent.trim() === name);
    for (const [name, field, expected] of [
      ['Velocity', 'velocity', 99],
      ['Gate', 'gate', 0.9],
      ['Repeats', 'repeats', 1],
      ['Chance', 'chance', 0.95],
      ['Offset', 'offset', -0.05]
    ] as const) {
      mode(name)?.click();
      await fixture.whenStable();
      expect(mode(name)?.getAttribute('aria-checked')).toBe('true');
      const slider = byLabel(`Kick step 5 ${name.toLowerCase()}`);
      expect(slider.getAttribute('role')).toBe('slider');
      slider.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
      await settle();
      await fixture.whenStable();
      expect(region().pattern.rows[0]?.steps[4]?.[field]).toBe(expected);
    }

    // The row length: type and Enter.
    const length = byLabel('Closed Hat length') as HTMLInputElement;
    expect(length.getAttribute('role')).toBe('spinbutton');
    length.value = '7';
    length.dispatchEvent(new Event('input'));
    length.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await settle();
    await fixture.whenStable();
    expect(region().pattern.rows[3]?.length).toBe(7);
    mode('On/Off')?.click();
    await fixture.whenStable();
    expect(element.querySelectorAll('[aria-label^="Closed Hat step "]')).toHaveLength(7);

    // Mute.
    byLabel('Mute Snare').click();
    await settle();
    expect(region().pattern.rows[1]?.muted).toBe(true);

    // Play mode, from its menu.
    byLabel('Snare play mode').click();
    await fixture.whenStable();
    const pingPong = [...document.querySelectorAll<HTMLElement>('[role="menuitemradio"]')].find(
      item => item.textContent.trim() === 'Ping-Pong'
    );
    pingPong?.click();
    await settle();
    expect(region().pattern.rows[1]?.playMode).toBe('pingPong');

    // Convert to MIDI.
    element.querySelector<HTMLButtonElement>('[data-testid="convert-pattern-to-midi"]')?.click();
    await settle();
    expect(project.patternRegionList()).toHaveLength(0);
    expect(project.midiRegionList()[0]?.notes.length).toBeGreaterThan(0);
  });

  it('reads notes typed as numbers or names', () => {
    expect(parseNote('60')).toBe(60);
    expect(parseNote('C3')).toBe(60);
    expect(parseNote('f#2')).toBe(54);
    expect(parseNote('B♭-1')).toBe(22);
    expect(parseNote('H2')).toBeNull();
  });

  it('draws pattern regions in the Tracks area as MIDI regions of their unrolled steps', () => {
    const track = { id: 'trk_1', color: '#e5554e', strip: { mute: false } } as Track;
    const step = { on: true, velocity: 100, gate: 1, repeats: 1, chance: 1, offset: 0 };
    const off = { ...step, on: false };
    const region: PatternRegion = {
      id: 'pat_1',
      trackId: 'trk_1',
      name: 'Beat',
      startTick: 3840,
      lengthTicks: 3840,
      muted: true,
      pattern: {
        rows: [
          {
            id: 'r1',
            kind: 'pad',
            note: 36,
            name: 'Kick',
            length: 4,
            stepTicks: null,
            playMode: 'forward',
            muted: false,
            steps: [step, off, off, off]
          }
        ],
        swing: 0,
        seed: 1,
        stepTicks: 240
      }
    };
    const { scene, regionIds } = buildScene({
      tracks: [track],
      regions: [],
      patternRegions: [region],
      framesPerTick: 25,
      beatsPerBar: 4,
      selected: new Set(['pat_1']),
      waveformKey: () => 0
    });
    expect(regionIds).toEqual(['pat_1']);
    expect(scene.items.start[0]).toBe(3840);
    expect(scene.items.length[0]).toBe(3840);
    expect(scene.regions?.flags[0]).toBe(ItemFlag.Midi | ItemFlag.Selected | ItemFlag.Muted);
    // A 4-step row cycles four times in a bar: notes at 0, 960, 1920, 2880 from the region's start.
    expect(Array.from(scene.regions?.notes?.start ?? [])).toEqual([0, 960, 1920, 2880]);
    expect(Array.from(scene.regions?.notes?.pitch ?? [])).toEqual([36, 36, 36, 36]);

    // The Mute tool and the Text tool edit pattern regions.
    const lookup = {
      regions: new Map(),
      midiRegions: new Map(),
      takeFolders: new Map(),
      patternRegions: new Map([['pat_1', region]]),
      endOf: () => 0,
      frames: () => 0,
      fileFrames: () => 0
    };
    expect(muteCommands(['pat_1'], lookup)).toEqual([{ type: 'setPatternRegion', regionId: 'pat_1', muted: false }]);
    expect(renameCommand('pat_1', 'Groove', lookup)).toEqual({
      type: 'setPatternRegion',
      regionId: 'pat_1',
      name: 'Groove'
    });
  });
});
