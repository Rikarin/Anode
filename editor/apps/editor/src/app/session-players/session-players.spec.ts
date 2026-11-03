import { TestBed } from '@angular/core/testing';
import type { PitchName } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { EditorsPane } from '../editors/editors-pane';
import { ChordTrackLane } from '../global/chord-track';
import { PitchSourceFields } from '../inspector/pitch-source-fields';
import { HistoryStore } from '../state/history.store';
import { PianoRollStore } from '../state/piano-roll.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { SessionPlayersStore } from '../state/session-players.store';
import { UiStore } from '../state/ui.store';
import { buildScene } from '../tracks/scene';
import { SessionPlayerEditor } from './session-player-editor';

/** A value the test needs to be there. */
function must<T>(value: T | null | undefined): T {
  if (value === null || value === undefined) {
    throw new Error('Expected a value.');
  }
  return value;
}

const Bar = 3840;
const c: PitchName = { step: 'c', alter: 0 };
const a: PitchName = { step: 'a', alter: 0 };

async function song() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  await server.createProject({ name: 'Players' });
  await settle();
  return { server, project };
}

async function stable(fixture: { whenStable(): Promise<unknown> }): Promise<void> {
  await settle();
  await fixture.whenStable();
  await settle();
}

describe('Session Players in the editor', () => {
  it('fetches each region’s generated notes and refetches them when the region or the chord track changes', async () => {
    const { project } = await song();
    const players = TestBed.inject(SessionPlayersStore);
    await project.execute([{ type: 'addSessionPlayerTrack', player: 'bass', regions: true }], 'New Bass Player');
    await settle();
    const region = must(players.regions()[0]);
    const first = players.notes().get(region.id);
    expect(first?.length).toBeGreaterThan(0);
    await players.set(region.id, { complexity: 0.95 }, 'Complexity');
    await settle();
    expect(must(players.notes().get(region.id)).length).toBeGreaterThan(must(first).length);
    const before = players.notes().get(region.id);
    await project.execute([{ type: 'addChords', chords: [{ tick: Bar, root: a, quality: 'm', bass: null }] }], 'Add');
    await settle();
    expect(players.notes().get(region.id)).not.toEqual(before);
    // The Tracks area draws the region with its notes.
    const scene = buildScene({
      tracks: project.trackList(),
      regions: [],
      sessionPlayerRegions: players.regions(),
      sessionPlayerNotes: players.notes(),
      framesPerTick: 25,
      beatsPerBar: 4,
      selected: new Set(),
      waveformKey: () => 0
    });
    expect(scene.regionIds).toContain(region.id);
    await project.execute([{ type: 'deleteRegions', regionIds: [region.id] }], 'Delete');
    await settle();
    expect(players.notes().has(region.id)).toBe(false);
  });

  it('brings the Session Player Editor for a region: the pad, a preset, the switches, Regenerate and Convert', async () => {
    const { server, project } = await song();
    await project.execute([{ type: 'addSessionPlayerTrack', player: 'drummer', regions: true }], 'New Drummer');
    await settle();
    const players = TestBed.inject(SessionPlayersStore);
    const region = must(players.regions()[0]);
    TestBed.inject(SelectionStore).select([region.id]);
    const pane = TestBed.createComponent(EditorsPane);
    pane.autoDetectChanges();
    await stable(pane);
    expect(TestBed.inject(UiStore).editorView()).toBe('sessionPlayer');

    const fixture = TestBed.createComponent(SessionPlayerEditor);
    fixture.autoDetectChanges();
    await stable(fixture);
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="session-player-title"]')?.textContent).toContain('Drummer');
    expect(element.querySelectorAll('[role="listbox"][aria-label="Styles"] [role="option"]')).toHaveLength(4);

    // The pad: arrows move it and send both numbers in one step.
    const pad = must(element.querySelector<HTMLElement>('[role="slider"][aria-roledescription="XY pad"]'));
    pad.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    await stable(fixture);
    const moved = must(must(server.current).sessionPlayerRegions[0]).parameters.pad;
    expect(moved.complexity).toBeCloseTo(region.parameters.pad.complexity + 0.05);
    expect(moved.intensity).toBe(region.parameters.pad.intensity);
    // A key held down keeps stepping from the last value, not from the Server's answer to an earlier step.
    pad.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    pad.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    pad.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    await stable(fixture);
    expect(must(must(server.current).sessionPlayerRegions[0]).parameters.pad.complexity).toBeCloseTo(
      moved.complexity + 0.15
    );

    // A preset, then a kit piece off (which clears the preset), each one undo step.
    const preset = must(
      element.querySelectorAll<HTMLButtonElement>('[role="listbox"][aria-label="Presets"] [role="option"]')[2]
    );
    preset.click();
    await stable(fixture);
    expect(must(must(server.current).sessionPlayerRegions[0]).parameters.presetId).toMatch(/driving$/);
    must(element.querySelector<HTMLButtonElement>('[data-piece="hiHat"]')).click();
    await stable(fixture);
    const parameters = must(must(server.current).sessionPlayerRegions[0]).parameters;
    expect(parameters.presetId).toBeNull();
    expect(parameters.drums?.pieces.find(p => p.piece === 'hiHat')?.on).toBe(false);
    await TestBed.inject(HistoryStore).undo();
    await settle();
    expect(
      must(must(server.current).sessionPlayerRegions[0]).parameters.drums?.pieces.find(p => p.piece === 'hiHat')?.on
    ).toBe(true);

    const seed = must(must(server.current).sessionPlayerRegions[0]).seed;
    must(element.querySelector<HTMLButtonElement>('[data-testid="session-player-regenerate"]')).click();
    await stable(fixture);
    expect(must(must(server.current).sessionPlayerRegions[0]).seed).not.toBe(seed);

    must(element.querySelector<HTMLButtonElement>('[data-testid="session-player-convert-pattern"]')).click();
    await settle();
    server.runJobs();
    await stable(fixture);
    expect(must(server.current).sessionPlayerRegions).toEqual([]);
    expect(must(server.current).patternRegions).toHaveLength(2); // eight bars: two patterns of four
  });

  it('shows a Session Player region’s notes read-only in the Piano Roll', async () => {
    const { project } = await song();
    const players = TestBed.inject(SessionPlayersStore);
    await project.execute([{ type: 'addSessionPlayerTrack', player: 'keyboard', regions: true }], 'New Keys');
    await settle();
    const region = must(players.regions()[0]);
    TestBed.inject(SelectionStore).select([region.id]);
    const roll = TestBed.inject(PianoRollStore);
    expect(roll.readOnly()).toBe(true);
    expect(roll.regions()[0]?.notes).toEqual(players.notes().get(region.id));
  });
});

describe('the chord track', () => {
  it('draws chords as symbols, edits one in its popover as one undo step, and parses typed symbols', async () => {
    const { server, project } = await song();
    await project.execute(
      [
        {
          type: 'addChords',
          chords: [
            { tick: 0, root: c, quality: '', bass: null },
            { tick: Bar, root: a, quality: 'm7', bass: null }
          ]
        }
      ],
      'Add Chords'
    );
    const fixture = TestBed.createComponent(ChordTrackLane);
    fixture.componentRef.setInput('originTick', 0);
    fixture.componentRef.setInput('pixelsPerTick', 0.05);
    fixture.autoDetectChanges();
    await stable(fixture);
    const element = fixture.nativeElement as HTMLElement;
    const chords = () => [...element.querySelectorAll<HTMLElement>('[data-chord]')].map(e => e.textContent.trim());
    expect(chords()).toEqual(['C', 'Am7']);

    const am7 = must(element.querySelector<HTMLButtonElement>('[data-chord="Am7"]'));
    am7.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await stable(fixture);
    const quality = must(document.querySelector<HTMLSelectElement>('[data-testid="chord-quality"]'));
    quality.value = '7';
    quality.dispatchEvent(new Event('change'));
    await stable(fixture);
    expect(chords()).toEqual(['C', 'A7']);
    await TestBed.inject(HistoryStore).undo();
    await stable(fixture);
    expect(chords()).toEqual(['C', 'Am7']);

    am7.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await stable(fixture);
    const symbol = must(document.querySelector<HTMLInputElement>('[data-testid="chord-symbol"]'));
    symbol.value = 'G/B';
    must(symbol.form).dispatchEvent(new Event('submit', { cancelable: true }));
    await stable(fixture);
    expect(must(server.current).chordTrack.chords[1]).toMatchObject({
      root: { step: 'g', alter: 0 },
      quality: '',
      bass: { step: 'b', alter: 0 }
    });

    // ←/→ move a chord by a beat; Delete removes it.
    must(element.querySelector<HTMLButtonElement>('[data-chord="G/B"]')).dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true })
    );
    await stable(fixture);
    expect(must(server.current).chordTrack.chords[1]?.tick).toBe(Bar + 960);
    must(element.querySelector<HTMLButtonElement>('[data-chord="G/B"]')).dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Delete', bubbles: true })
    );
    await stable(fixture);
    expect(chords()).toEqual(['C']);
  });

  it('sets a region’s Pitch Source and key from the inspector', async () => {
    const { server, project } = await song();
    await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: true }], 'New Track');
    const trackId = must(project.trackIds()[0]);
    await project.execute([{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: Bar }], 'New Region');
    const regionId = must(must(server.current).midiRegions[0]).id;
    const fixture = TestBed.createComponent(PitchSourceFields);
    fixture.componentRef.setInput('regionId', regionId);
    fixture.componentRef.setInput('mode', 'off');
    fixture.componentRef.setInput('regionKey', null);
    fixture.autoDetectChanges();
    await stable(fixture);
    const element = fixture.nativeElement as HTMLElement;
    const source = must(element.querySelector<HTMLSelectElement>('[data-testid="region-pitch-source"]'));
    source.value = 'root';
    source.dispatchEvent(new Event('change'));
    await stable(fixture);
    expect(must(server.current).midiRegions[0]?.pitchSource).toBe('root');
    const key = must(element.querySelector<HTMLSelectElement>('[data-testid="region-key"]'));
    key.value = 'a:minor';
    key.dispatchEvent(new Event('change'));
    await stable(fixture);
    expect(must(server.current).midiRegions[0]?.key).toEqual({ tonic: a, mode: 'minor' });
  });
});
