import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type { Chord, EditCommand, PitchName, ScaleChange } from '@anode/api-client';
import {
  chordBlock,
  chordLabel,
  ChordQualityOptions,
  chordSpans,
  chordSymbol,
  parseChordSymbol,
  pitchFromKey,
  pitchKey,
  pitchNameText,
  PitchSpellings,
  ScaleOptions,
  scaleText
} from '@anode/audio-ui';
import { AnChordLane, type ChordLaneChord, type ChordLaneScale, type ChordLaneTarget } from '@anode/audio-ui/lazy';
import { XuiPopoverImports } from '@xui/popover';
import { Field, selectValue } from '../control/styles';
import { ProjectStore } from '../state/project.store';
import { TracksViewStore } from '../state/tracks-view.store';
import { snapBy } from '../tracks/snap';

/** What the editor popover shows: a chord, or a key and scale change by index; where it is anchored. */
type Editing = { kind: 'chord'; id: string; x: number } | { kind: 'scale'; index: number; x: number };

/** The tonics a key and scale change offers (the chord track's spellings). */
const Tonics = PitchSpellings;

function samePitch(a: PitchName | null, b: PitchName | null): boolean {
  return a?.step === b?.step && a?.alter === b?.alter;
}

/**
 * The chord track (Phase 11, ADR-0014) in the global tracks: its chords drawn as symbols over the key and scale
 * changes, a popover to edit a chord (a typed symbol, or root, quality and slash bass) or a key change, and the drags
 * and keys of `an-chord-lane`. Every edit is one undo step; positions snap by the Tracks area's snap mode.
 */
@Component({
  selector: 'an-chord-track',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnChordLane, XuiPopoverImports],
  host: { class: 'relative block h-full' },
  template: `
    <an-chord-lane
      label="Chord track"
      [chords]="blocks()"
      [scales]="scaleTags()"
      (opened)="open($event)"
      (moved)="moveBy($event.id, $event.dx)"
      (nudged)="nudge($event.id, $event.steps)"
      (removed)="remove($event)"
      (added)="add($event)"
      (scaleOpened)="openScale($event)"
      (scaleAdded)="addScale($event)"
    />
    <div
      class="pointer-events-none absolute bottom-0 h-px w-px"
      [style.left.px]="editing()?.x ?? 0"
      [xuiPopover]="editor"
      placement="bottom-start"
      ariaLabel="Edit chord"
      role="dialog"
      [open]="editing() !== null"
      (openChange)="$event || editing.set(null)"
    ></div>
    <ng-template #editor>
      @if (editedChord(); as chord) {
        <form
          class="grid w-60 grid-cols-[4rem_1fr] items-center gap-x-2 gap-y-1.5 p-2 text-xs"
          (submit)="typed($event)"
        >
          <label class="text-foreground-muted" for="chord-symbol">Chord</label>
          <input
            #symbol
            id="chord-symbol"
            [class]="field"
            data-testid="chord-symbol"
            autocomplete="off"
            [value]="symbolOf(chord)"
            [attr.aria-invalid]="invalid() || null"
            (input)="invalid.set(false)"
          />
          <label class="text-foreground-muted" for="chord-root">Root</label>
          <select id="chord-root" [class]="field" [value]="pitchKey(chord.root)" (change)="setRoot(chord, $event)">
            @for (pitch of spellings; track $index) {
              <option [value]="pitchKey(pitch)">{{ pitchNameText(pitch) }}</option>
            }
          </select>
          <label class="text-foreground-muted" for="chord-quality">Quality</label>
          <select
            id="chord-quality"
            [class]="field"
            data-testid="chord-quality"
            [value]="chord.quality"
            (change)="edit(chord, { quality: selectValue($event) })"
          >
            @for (option of qualities; track option.quality) {
              <option [value]="option.quality">
                {{ option.name }}{{ option.symbol ? ' (' + option.symbol + ')' : '' }}
              </option>
            }
          </select>
          <label class="text-foreground-muted" for="chord-bass">Bass</label>
          <select
            id="chord-bass"
            [class]="field"
            [value]="chord.bass ? pitchKey(chord.bass) : ''"
            (change)="setBass(chord, $event)"
          >
            <option value="">Root</option>
            @for (pitch of spellings; track $index) {
              <option [value]="pitchKey(pitch)">{{ pitchNameText(pitch) }}</option>
            }
          </select>
          <div class="col-span-2 flex justify-between pt-1">
            <button
              type="button"
              class="bezel text-foreground h-6 px-2 text-[11px] font-semibold"
              (click)="remove(chord.id)"
            >
              Delete
            </button>
            <button type="submit" class="bezel text-foreground h-6 px-2 text-[11px] font-semibold">Done</button>
          </div>
        </form>
      } @else if (editedScale(); as scale) {
        <div
          class="grid w-60 grid-cols-[4rem_1fr] items-center gap-x-2 gap-y-1.5 p-2 text-xs"
          role="group"
          aria-label="Key"
        >
          <label class="text-foreground-muted" for="scale-tonic">Tonic</label>
          <select id="scale-tonic" [class]="field" [value]="pitchKey(scale.tonic)" (change)="setTonic(scale, $event)">
            @for (pitch of tonics; track $index) {
              <option [value]="pitchKey(pitch)">{{ pitchNameText(pitch) }}</option>
            }
          </select>
          <label class="text-foreground-muted" for="scale-kind">Scale</label>
          <select id="scale-kind" [class]="field" [value]="scale.scale" (change)="setScale(scale, $event)">
            @for (option of scales; track option.scale) {
              <option [value]="option.scale">{{ option.name }}</option>
            }
          </select>
          <div class="col-span-2 flex justify-between pt-1">
            <button
              type="button"
              class="bezel text-foreground h-6 px-2 text-[11px] font-semibold"
              (click)="removeScale(scale)"
            >
              Delete
            </button>
            <button
              type="button"
              class="bezel text-foreground h-6 px-2 text-[11px] font-semibold"
              (click)="editing.set(null)"
            >
              Done
            </button>
          </div>
        </div>
      }
    </ng-template>
  `
})
export class ChordTrackLane {
  private readonly project = inject(ProjectStore);
  private readonly view = inject(TracksViewStore);
  readonly originTick = input.required<number>();
  readonly pixelsPerTick = input.required<number>();

  protected readonly field = Field;
  protected readonly selectValue = selectValue;
  protected readonly pitchKey = pitchKey;
  protected readonly pitchNameText = pitchNameText;
  protected readonly spellings = PitchSpellings;
  protected readonly tonics = Tonics;
  protected readonly qualities = ChordQualityOptions;
  protected readonly scales = ScaleOptions;

  protected readonly editing = signal<Editing | null>(null);
  protected readonly invalid = signal(false);

  private readonly chords = computed(() => this.project.chordTrack().chords);
  protected readonly blocks = computed<ChordLaneChord[]>(() => {
    const meter = this.project.meter();
    const editing = this.editing();
    return chordSpans(this.chords(), meter.barTicksAt(this.chords().at(-1)?.tick ?? 0)).map(span => ({
      id: span.chord.id,
      symbol: chordSymbol(span.chord),
      label: `${chordLabel(span.chord)}, bar ${Math.floor(meter.barAt(span.startTick)) + 1}`,
      selected: editing?.kind === 'chord' && editing.id === span.chord.id,
      ...chordBlock(span, tick => this.x(tick))
    }));
  });
  protected readonly scaleTags = computed<ChordLaneScale[]>(() =>
    this.project.chordTrack().scales.map((scale, index) => ({ index, text: scaleText(scale), x: this.x(scale.tick) }))
  );
  protected readonly editedChord = computed(() => {
    const editing = this.editing();
    return editing?.kind === 'chord' ? (this.chords().find(c => c.id === editing.id) ?? null) : null;
  });
  protected readonly editedScale = computed(() => {
    const editing = this.editing();
    return editing?.kind === 'scale' ? (this.project.chordTrack().scales[editing.index] ?? null) : null;
  });

  private x(tick: number): number {
    return (tick - this.originTick()) * this.pixelsPerTick();
  }

  private tickAt(x: number): number {
    return Math.max(0, this.originTick() + x / this.pixelsPerTick());
  }

  private snap(tick: number): number {
    const mode = this.view.snap() ? this.view.snapMode() : 'ticks';
    return Math.max(
      0,
      Math.round(
        snapBy(tick, mode, {
          meter: this.project.meter(),
          tempo: this.project.tempoMap(),
          sampleRate: this.project.info()?.sampleRate ?? 48_000,
          pixelsPerTick: this.pixelsPerTick()
        })
      )
    );
  }

  protected symbolOf(chord: Chord): string {
    return chordSymbol(chord).replaceAll('♯', '#').replaceAll('♭', 'b');
  }

  protected open(target: ChordLaneTarget): void {
    const chord = this.chords().find(c => c.id === target.id);
    if (chord) {
      this.invalid.set(false);
      this.editing.set({ kind: 'chord', id: chord.id, x: this.x(chord.tick) });
    }
  }

  protected openScale(target: ChordLaneTarget): void {
    const index = Number(target.id);
    const scale = this.project.chordTrack().scales[index];
    if (scale) {
      this.editing.set({ kind: 'scale', index, x: this.x(scale.tick) });
    }
  }

  /** A typed symbol replaces the chord (Return in the field); Done with an unchanged one just closes. */
  protected typed(event: Event): void {
    event.preventDefault();
    const chord = this.editedChord();
    const input = (event.target as HTMLFormElement).querySelector<HTMLInputElement>('#chord-symbol');
    if (!chord || !input) {
      return;
    }
    const parsed = parseChordSymbol(input.value);
    if (!parsed) {
      this.invalid.set(true);
      return;
    }
    this.edit(chord, parsed);
    this.editing.set(null);
  }

  protected setRoot(chord: Chord, event: Event): void {
    const root = pitchFromKey(selectValue(event));
    if (root) {
      this.edit(chord, { root });
    }
  }

  protected setBass(chord: Chord, event: Event): void {
    this.edit(chord, { bass: pitchFromKey(selectValue(event)) });
  }

  /** One `editChords` (one undo step); nothing when nothing changes. */
  protected edit(chord: Chord, changes: Partial<Pick<Chord, 'root' | 'quality' | 'bass' | 'tick'>>): void {
    const next = { ...chord, ...changes };
    if (next.bass && samePitch(next.bass, next.root)) {
      next.bass = null;
    }
    if (
      next.tick === chord.tick &&
      next.quality === chord.quality &&
      samePitch(next.root, chord.root) &&
      samePitch(next.bass, chord.bass)
    ) {
      return;
    }
    void this.project.execute([{ type: 'editChords', chords: [next] }], 'Edit Chord');
  }

  protected moveBy(id: string, dx: number): void {
    const chord = this.chords().find(c => c.id === id);
    if (chord) {
      this.moveTo(chord, this.snap(chord.tick + dx / this.pixelsPerTick()));
    }
  }

  /** ←/→: a beat (⇧: a bar) earlier or later. */
  protected nudge(id: string, steps: number): void {
    const chord = this.chords().find(c => c.id === id);
    if (chord) {
      const beat = this.project.meter().beatTicksAt(chord.tick);
      this.moveTo(chord, Math.max(0, chord.tick + steps * beat));
    }
  }

  private moveTo(chord: Chord, tick: number): void {
    if (tick === chord.tick) {
      return;
    }
    // A chord moved onto another replaces it.
    const taken = this.chords().find(c => c.id !== chord.id && c.tick === tick);
    const commands: EditCommand[] = [
      ...(taken ? [{ type: 'deleteChords' as const, chordIds: [taken.id] }] : []),
      { type: 'editChords', chords: [{ ...chord, tick }] }
    ];
    void this.project.execute(commands, 'Move Chord');
  }

  protected remove(id: string): void {
    this.editing.set(null);
    void this.project.execute([{ type: 'deleteChords', chordIds: [id] }], 'Delete Chord');
  }

  /** A double-click on an empty stretch: the chord there now (else the key's tonic chord) at the snapped tick. */
  protected async add(x: number): Promise<void> {
    const tick = this.snap(this.tickAt(x));
    const sounding = [...this.chords()].reverse().find(c => c.tick <= tick);
    const key = this.project
      .chordTrack()
      .scales.filter(s => s.tick <= tick)
      .at(-1);
    const root = sounding?.root ?? key?.tonic ?? { step: 'c', alter: 0 };
    const quality = sounding?.quality ?? (key && key.scale !== 'major' && key.scale !== 'lydian' ? 'm' : '');
    const result = await this.project.execute(
      [{ type: 'addChords', chords: [{ tick, root, quality, bass: null }] }],
      'Add Chord'
    );
    const added = this.chords().find(c => c.tick === tick);
    if (result && added) {
      this.editing.set({ kind: 'chord', id: added.id, x: this.x(tick) });
    }
  }

  protected addScale(x: number): void {
    const tick = this.project.meter().snapBar(this.tickAt(x));
    const scales = this.project.chordTrack().scales;
    if (scales.some(s => s.tick === tick)) {
      return;
    }
    const before = scales.filter(s => s.tick < tick).at(-1);
    const added: ScaleChange = {
      tick,
      tonic: before?.tonic ?? { step: 'c', alter: 0 },
      scale: before?.scale ?? 'major'
    };
    const next = [...scales, added].sort((a, b) => a.tick - b.tick);
    void this.setScales(next, 'Add Key Change').then(() =>
      this.editing.set({ kind: 'scale', index: next.indexOf(added), x: this.x(tick) })
    );
  }

  protected setTonic(scale: ScaleChange, event: Event): void {
    const tonic = pitchFromKey(selectValue(event));
    if (tonic) {
      this.replaceScale(scale, { ...scale, tonic });
    }
  }

  protected setScale(scale: ScaleChange, event: Event): void {
    const kind = ScaleOptions.find(o => o.scale === selectValue(event))?.scale;
    if (kind) {
      this.replaceScale(scale, { ...scale, scale: kind });
    }
  }

  protected removeScale(scale: ScaleChange): void {
    this.editing.set(null);
    void this.setScales(
      this.project.chordTrack().scales.filter(s => s !== scale),
      'Delete Key Change'
    );
  }

  private replaceScale(scale: ScaleChange, next: ScaleChange): void {
    void this.setScales(
      this.project.chordTrack().scales.map(s => (s === scale ? next : s)),
      'Key Change'
    );
  }

  private setScales(scales: ScaleChange[], label: string): Promise<unknown> {
    return this.project.execute([{ type: 'setScales', scales }], label);
  }
}
