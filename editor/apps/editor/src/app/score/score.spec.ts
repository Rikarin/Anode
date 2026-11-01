import { TestBed } from '@angular/core/testing';
import { StaffStyleIds } from '@anode/api-client';
import { type NotationRenderer, type RenderOptions, VerovioEngine, type VerovioToolkitLike } from '@anode/notation';
import { XuiToastService } from '@xui/toast';
import { VerovioToolkit } from 'verovio/esm';
import createVerovioModule from 'verovio/wasm';
import { EventStream } from '../core/events';
import { provideFakeServer } from '../core/fake/fake-backend';
import { FakeAnodeServer } from '../core/fake/fake-server';
import { settle } from '../core/fake/testing';
import { RegionNotationInspector, TrackStaffStyle } from '../inspector/score-inspector';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { ScoreEditor } from './score-editor';
import { ScoreLayoutWindow } from './score-layout-window';
import { ScoreSetsWindow } from './score-sets-window';
import { NOTATION_RENDERER, RefetchDelayMs, ScoreStore } from './score.store';
import { StaffStylesWindow } from './staff-styles-window';

let toolkit: VerovioToolkitLike;

/** The renderer in-process: the worker's engine on the real Verovio, so the tests read real SVG. */
class InProcessRenderer implements NotationRenderer {
  private readonly engine = new VerovioEngine(toolkit);
  renders = 0;

  render(mei: string, options: RenderOptions) {
    this.renders++;
    return Promise.resolve(this.engine.render(mei, options).pages);
  }

  dispose(): void {
    // nothing to stop
  }
}

const wait = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

async function setUp(
  notes: [number, number, number][] = [
    [64, 0, 960],
    [67, 960, 960],
    [72, 1920, 1920]
  ]
) {
  const server = new FakeAnodeServer();
  const renderers: InProcessRenderer[] = [];
  TestBed.configureTestingModule({
    providers: [
      ...provideFakeServer(server),
      { provide: XuiToastService, useValue: null },
      {
        provide: NOTATION_RENDERER,
        useValue: () => {
          const renderer = new InProcessRenderer();
          renderers.push(renderer);
          return renderer;
        }
      }
    ]
  });
  TestBed.inject(EventStream).start();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Score' }));
  await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: false, name: 'Piano' }], 'Track');
  const trackId = project.trackList()[0]?.id ?? '';
  await project.execute([{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 7680 }], 'Region');
  const regionId = [...project.midiRegions().keys()][0] ?? '';
  await project.execute(
    [
      {
        type: 'addNotes',
        regionId,
        notes: notes.map(([pitch, startTick, lengthTicks]) => ({
          pitch,
          startTick,
          lengthTicks,
          velocity: 90,
          channel: 1
        }))
      }
    ],
    'Notes'
  );
  await settle();
  const noteIds =
    project
      .midiRegions()
      .get(regionId)
      ?.notes.map(n => n.id) ?? [];
  return { server, project, trackId, regionId, noteIds, renderers };
}

async function engraved(fixture: { whenStable(): Promise<unknown>; detectChanges(): void; nativeElement: unknown }) {
  await wait(RefetchDelayMs + 20);
  await vi.waitFor(
    async () => {
      await settle();
      fixture.detectChanges();
      await fixture.whenStable();
      expect((fixture.nativeElement as HTMLElement).querySelector('[data-page] svg')).not.toBeNull();
    },
    { timeout: 5000 }
  );
}

describe('the Score Editor', () => {
  beforeAll(async () => {
    toolkit = new VerovioToolkit(await createVerovioModule());
    // jsdom has no pointer capture.
    const element = HTMLElement.prototype as Partial<HTMLElement>;
    element.setPointerCapture ??= () => undefined;
  }, 30_000);

  it('refetches the score after changes (debounced) and engraves the visible pages first', async () => {
    const { project, trackId, regionId, renderers } = await setUp();
    const store = TestBed.runInInjectionContext(() => new ScoreStore());
    store.query.set({ part: trackId });
    TestBed.tick();
    await vi.waitFor(() => expect(store.document()?.view.parts[0]?.trackId).toBe(trackId), { timeout: 5000 });
    expect(store.pages()).toHaveLength(1);
    expect(store.index()?.notes.size).toBe(3);
    // Two quick edits: one refetch, one render.
    const renders = renderers.at(-1)?.renders ?? 0;
    await project.execute([{ type: 'setRegionNotation', regionIds: [regionId], displayQuantizeTicks: 960 }], 'Q');
    await project.execute([{ type: 'setRegionNotation', regionIds: [regionId], maxDots: 2 }], 'Dots');
    TestBed.tick();
    await vi.waitFor(() => expect(renderers.at(-1)?.renders).toBe(renders + 1), { timeout: 5000 });
    store.view.set('page');
    TestBed.tick();
    await vi.waitFor(() => expect(store.pages()[0]?.svg).toContain('class="page-margin"'), { timeout: 5000 });
    expect(store.styleOf(trackId)?.id).toBe(StaffStyleIds.piano);
  });

  it('shows the Piano Roll track’s score, selects a clicked note and deletes it in one undo step', async () => {
    const { project, regionId, noteIds } = await setUp();
    TestBed.inject(SelectionStore).select([regionId]);
    const fixture = TestBed.createComponent(ScoreEditor);
    await engraved(fixture);
    const element = fixture.nativeElement as HTMLElement;
    const first = noteIds[0] as number;
    const head = element.querySelector(`[data-page] #n${first} .notehead use`);
    expect(head).not.toBeNull();
    head?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 0, clientY: 0 }));
    head?.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, button: 0, clientX: 0, clientY: 0 }));
    await settle();
    expect([...TestBed.inject(SelectionStore).notes()]).toEqual([first]);
    fixture.detectChanges();
    await settle();
    expect(element.querySelector(`#n${first}`)?.classList.contains('an-selected')).toBe(true);
    const scroller = element.querySelector('[data-testid="score-scroller"]') as HTMLElement;
    scroller.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }));
    await settle();
    expect(
      project
        .midiRegions()
        .get(regionId)
        ?.notes.map(n => n.id)
    ).toEqual(noteIds.slice(1));
    fixture.destroy();
  });

  it('puts a Part box symbol on the selected notes at once, and enters chord symbols', async () => {
    const { project, regionId, noteIds } = await setUp();
    TestBed.inject(SelectionStore).select([regionId]);
    const fixture = TestBed.createComponent(ScoreEditor);
    await engraved(fixture);
    const element = fixture.nativeElement as HTMLElement;
    TestBed.inject(SelectionStore).selectNotes(noteIds.slice(0, 2));
    (element.querySelector('[data-testid="part-staccato"]') as HTMLButtonElement).click();
    await settle();
    expect(project.score().symbols.map(s => [s.kind, s.noteId])).toEqual([
      ['staccato', noteIds[0]],
      ['staccato', noteIds[1]]
    ]);
    (element.querySelector('[data-testid="part-slur"]') as HTMLButtonElement).click();
    await settle();
    expect(project.score().symbols.at(-1)).toMatchObject({ kind: 'slur', noteId: noteIds[0], endNoteId: noteIds[1] });
    fixture.destroy();
  });

  it('enters lyrics: a hyphen joins syllables, Tab moves on', async () => {
    const { project, regionId, noteIds } = await setUp();
    TestBed.inject(SelectionStore).select([regionId]);
    const fixture = TestBed.createComponent(ScoreEditor);
    await engraved(fixture);
    const element = fixture.nativeElement as HTMLElement;
    const [a, b, c] = noteIds as [number, number, number];
    (element.querySelector('[data-testid="part-lyric"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    element
      .querySelector(`[data-page] #n${a} .notehead use`)
      ?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0 }));
    fixture.detectChanges();
    await fixture.whenStable();
    const type = async (text: string, key: string) => {
      const field = element.querySelector<HTMLInputElement>('[data-testid="score-entry-lyric"]');
      expect(field).not.toBeNull();
      if (field) {
        field.value = text;
        field.dispatchEvent(new Event('input', { bubbles: true }));
        field.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
      }
      await settle();
      fixture.detectChanges();
      await fixture.whenStable();
    };
    await type('Hel', '-');
    await type('lo', 'Tab');
    await type('world', 'Enter');
    expect(project.score().lyrics.map(l => [l.noteId, l.text, l.syllabic])).toEqual([
      [a, 'Hel', 'begin'],
      [b, 'lo', 'end'],
      [c, 'world', 'single']
    ]);
    fixture.destroy();
  });
});

describe('score windows and inspector', () => {
  beforeAll(async () => {
    toolkit = new VerovioToolkit(await createVerovioModule());
  }, 30_000);

  it('makes a score set and extracts a part', async () => {
    const { project, trackId } = await setUp();
    const ui = TestBed.inject(UiStore);
    const fixture = TestBed.createComponent(ScoreSetsWindow);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    (element.querySelector('[data-testid="score-set-new"]') as HTMLButtonElement).click();
    await settle();
    fixture.detectChanges();
    const set = project.score().sets[0];
    expect(set).toMatchObject({ name: 'Full Score', trackIds: [trackId] });
    expect(set?.id).toMatch(/^scs_/);
    (element.querySelector('[data-testid="score-set-open"]') as HTMLButtonElement).click();
    expect(ui.scoreWindow()).toEqual({ setId: set?.id, partTrackId: null });
    (element.querySelector('[data-testid="score-set-extract"]') as HTMLButtonElement).click();
    expect(ui.scoreWindow()).toEqual({ setId: null, partTrackId: trackId });
  });

  it('duplicates a factory staff style and assigns it to the selected track', async () => {
    const { project, trackId } = await setUp();
    TestBed.inject(SelectionStore).selectTrack(trackId);
    const fixture = TestBed.createComponent(StaffStylesWindow);
    await settle();
    fixture.detectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    (element.querySelector(`[data-testid="staff-style-${StaffStyleIds.clarinetBb}"]`) as HTMLButtonElement).click();
    fixture.detectChanges();
    (element.querySelector('[data-testid="staff-style-duplicate"]') as HTMLButtonElement).click();
    await settle();
    fixture.detectChanges();
    const copy = project.score().staffStyles[0];
    expect(copy).toMatchObject({ name: 'B♭ Clarinet Copy', staves: [{ transpose: 2 }] });
    const name = element.querySelector<HTMLInputElement>('[data-testid="staff-style-name"]');
    if (name) {
      name.value = 'Clarinet';
      name.dispatchEvent(new Event('change'));
    }
    await settle();
    expect(project.score().staffStyles[0]?.name).toBe('Clarinet');
    (element.querySelector('[data-testid="staff-style-assign"]') as HTMLButtonElement).click();
    await settle();
    expect(project.track(trackId)?.staffStyleId).toBe(copy?.id);
  });

  it('edits the layout: preset, title and transposing score', async () => {
    const { project } = await setUp();
    const fixture = TestBed.createComponent(ScoreLayoutWindow);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const preset = element.querySelector<HTMLSelectElement>('[data-testid="layout-preset"]');
    if (preset) {
      preset.value = 'Letter';
      preset.dispatchEvent(new Event('change'));
    }
    await settle();
    expect(project.score().layout).toMatchObject({ pageWidthMm: 215.9, pageHeightMm: 279.4 });
    const title = element.querySelector<HTMLInputElement>('[data-testid="layout-title"]');
    if (title) {
      title.value = 'Etude';
      title.dispatchEvent(new Event('change'));
    }
    await settle();
    (element.querySelector('[data-testid="layout-transposing"]') as HTMLInputElement).click();
    await settle();
    expect(project.score().layout).toMatchObject({ title: 'Etude', transposingScore: true });
  });

  it('sets the selected regions’ score settings and a track’s staff style from the inspector', async () => {
    const { project, trackId, regionId } = await setUp();
    TestBed.inject(SelectionStore).select([regionId]);
    const region = TestBed.createComponent(RegionNotationInspector);
    region.componentRef.setInput('region', project.midiRegions().get(regionId));
    await region.whenStable();
    const quantize = (region.nativeElement as HTMLElement).querySelector<HTMLSelectElement>(
      '[data-testid="region-display-quantize"]'
    );
    if (quantize) {
      quantize.value = '480';
      quantize.dispatchEvent(new Event('change'));
    }
    await settle();
    expect(project.midiRegions().get(regionId)?.notation.displayQuantizeTicks).toBe(480);
    (
      (region.nativeElement as HTMLElement).querySelector('[data-testid="region-syncopation"]') as HTMLInputElement
    ).click();
    await settle();
    expect(project.midiRegions().get(regionId)?.notation.syncopation).toBe(true);

    const style = TestBed.createComponent(TrackStaffStyle);
    style.componentRef.setInput('track', project.track(trackId));
    await settle();
    style.detectChanges();
    await style.whenStable();
    const select = (style.nativeElement as HTMLElement).querySelector<HTMLSelectElement>(
      '[data-testid="track-staff-style"]'
    );
    expect(select?.value).toBe(StaffStyleIds.piano);
    if (select) {
      select.value = StaffStyleIds.trumpetBb;
      select.dispatchEvent(new Event('change'));
    }
    await settle();
    expect(project.track(trackId)?.staffStyleId).toBe(StaffStyleIds.trumpetBb);
  });
});
