import { ChangeDetectionStrategy, Component, computed, effect, inject, linkedSignal } from '@angular/core';
import type {
  BassSwitches,
  DrummerSwitches,
  DrumPiece,
  KeysLeftHand,
  KeysSwitches,
  KeysVoicing,
  SessionPlayerKind,
  SessionPlayerRegion
} from '@anode/api-client';
import { AnKnob } from '@anode/audio-ui';
import { AnSessionPad, type SessionPadMarker, type SessionPadValue } from '@anode/audio-ui/lazy';
import { Band, Field, selectValue } from '../control/styles';
import { noteName } from '../midi/midi-math';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { playerOf, SessionPlayersStore } from '../state/session-players.store';

const PieceNames: Readonly<Record<DrumPiece, string>> = {
  kick: 'Kick',
  snare: 'Snare',
  hiHat: 'Hi-Hat',
  toms: 'Toms',
  cymbals: 'Cymbals',
  percussion: 'Percussion'
};

const PlayerNames: Readonly<Record<SessionPlayerKind, string>> = {
  drummer: 'Drummer',
  bass: 'Bass Player',
  keyboard: 'Keyboard Player'
};

const Voicings: readonly { value: KeysVoicing; label: string }[] = [
  { value: 'close', label: 'Close' },
  { value: 'spread', label: 'Spread' },
  { value: 'shell', label: 'Shell' }
];

const LeftHands: readonly { value: KeysLeftHand; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'root', label: 'Root' },
  { value: 'octaves', label: 'Octaves' },
  { value: 'rootFifth', label: 'Root and Fifth' }
];

/** Notes for a range select, from `low` to `high`. */
function noteChoices(low: number, high: number): { note: number; name: string }[] {
  return Array.from({ length: high - low + 1 }, (_, i) => ({ note: low + i, name: noteName(low + i) }));
}

/**
 * The Session Player Editor (Logic's Drummer and Session Player Editor, Phase 11) for the selected Session Player
 * region, or the selected Session Player track's first: the style and preset browser on the left, the XY pad
 * (complexity and intensity) with fills, swing and feel in the middle, the player's own switches on the right (the
 * Drummer's kit pieces and their pattern variations and the rhythm source it follows; the bass's range, slides and
 * dead notes; the keys' voicing, range, left hand and rhythm), and Follow, Regenerate and Convert in the toolbar. Each
 * change is one `setSessionPlayerRegion` (an XY drag sends both axes when it ends); the region keeps its seed.
 */
@Component({
  selector: 'an-session-player-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnKnob, AnSessionPad],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Session Player Editor' },
  template: `
    @if (region(); as r) {
      <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2" role="toolbar" aria-label="Session Player">
        <span class="text-foreground min-w-0 truncate text-xs font-semibold" data-testid="session-player-title">
          {{ playerName() }}: {{ r.name }}
        </span>
        <span class="text-foreground-muted readout truncate text-[11px]">{{ styleName() }}</span>
        <span class="flex-1"></span>
        @if (player() !== 'drummer') {
          <button
            type="button"
            class="bezel h-6.5 px-2 text-[11px] font-semibold"
            data-testid="session-player-follow"
            [class.text-lit]="r.parameters.followChords"
            [class.text-foreground]="!r.parameters.followChords"
            [attr.aria-pressed]="r.parameters.followChords"
            (click)="set({ followChords: !r.parameters.followChords }, 'Follow Chord Track')"
          >
            Follow Chord Track
          </button>
        }
        <button
          type="button"
          class="bezel text-foreground h-6.5 px-2 text-[11px] font-semibold"
          data-testid="session-player-regenerate"
          (click)="players.regenerate([r.id])"
        >
          Regenerate
        </button>
        <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold" role="group" aria-label="Convert">
          <button type="button" class="px-2" data-testid="session-player-convert-midi" (click)="convert('midi')">
            Convert to MIDI
          </button>
          @if (player() === 'drummer') {
            <button
              type="button"
              class="px-2"
              data-testid="session-player-convert-pattern"
              (click)="convert('pattern')"
            >
              Convert to Pattern
            </button>
          }
        </div>
      </div>
      <div class="flex min-h-0 flex-1 overflow-auto">
        <div class="border-hairline flex w-56 shrink-0 flex-col border-e" role="group" aria-label="Styles and presets">
          <h3 [class]="band">Styles</h3>
          <div class="min-h-0 flex-1 overflow-auto" role="listbox" aria-label="Styles">
            @for (style of styles(); track style.id) {
              <button
                type="button"
                role="option"
                class="hover:bg-hover-overlay flex h-7 w-full items-center gap-2 px-3 text-start text-xs"
                [class.bg-primary/25]="style.id === r.parameters.styleId"
                [attr.aria-selected]="style.id === r.parameters.styleId"
                [attr.data-style]="style.id"
                (click)="setStyle(style.id)"
              >
                <span class="text-foreground min-w-0 flex-1 truncate font-semibold">{{ style.name }}</span>
                <span class="text-foreground-muted text-[10px]">{{ style.synthPlayer ? 'Synth' : style.genre }}</span>
              </button>
            } @empty {
              <p class="text-foreground-muted px-3 py-2 text-xs">Loading styles…</p>
            }
          </div>
          <h3 [class]="band">Presets</h3>
          <div class="max-h-36 overflow-auto" role="listbox" aria-label="Presets">
            @for (preset of presets(); track preset.id) {
              <button
                type="button"
                role="option"
                class="hover:bg-hover-overlay flex h-7 w-full items-center px-3 text-start text-xs"
                [class.bg-primary/25]="preset.id === r.parameters.presetId"
                [attr.aria-selected]="preset.id === r.parameters.presetId"
                (click)="setPreset(preset.id)"
              >
                <span class="text-foreground truncate">{{ preset.name }}</span>
              </button>
            }
          </div>
        </div>
        <div class="flex shrink-0 flex-col items-center gap-3 p-3">
          <an-session-pad
            [label]="r.name"
            [value]="pad()"
            [others]="others()"
            (valueChange)="draft.set($event)"
            (committed)="commitPad($event)"
          />
          <div class="flex gap-3">
            <an-knob
              label="Fills"
              unit="%"
              [min]="0"
              [max]="100"
              [defaultValue]="50"
              [value]="r.parameters.fillAmount * 100"
              (committed)="set({ fillAmount: $event / 100 }, 'Fills')"
            />
            <an-knob
              label="Swing"
              unit="%"
              [min]="0"
              [max]="100"
              [defaultValue]="0"
              [value]="r.parameters.swing * 100"
              (committed)="set({ swing: $event / 100 }, 'Swing')"
            />
            <an-knob
              label="Feel"
              [min]="-100"
              [max]="100"
              [defaultValue]="0"
              [format]="feelText"
              [value]="r.parameters.feel * 100"
              (committed)="set({ feel: $event / 100 }, 'Feel')"
            />
          </div>
          <label class="text-foreground flex items-center gap-1.5 text-xs">
            <input
              type="checkbox"
              class="accent-primary size-3"
              data-testid="session-player-section-fills"
              [checked]="r.parameters.fillAtSectionEnds"
              (change)="set({ fillAtSectionEnds: !r.parameters.fillAtSectionEnds }, 'Fills at Section Ends')"
            />
            Fills at section ends
          </label>
        </div>
        <div
          class="border-hairline min-w-64 flex-1 border-s"
          role="group"
          [attr.aria-label]="playerName() + ' settings'"
        >
          @if (r.parameters.drums; as drums) {
            <h3 [class]="band">Kit</h3>
            <div class="grid grid-cols-[6rem_1fr] items-center gap-x-2 gap-y-1.5 px-3 py-2 text-xs">
              @for (piece of drums.pieces; track piece.piece) {
                <button
                  type="button"
                  class="bezel h-6 px-2 text-start text-[11px] font-semibold"
                  [class.text-lit]="piece.on"
                  [class.text-foreground-muted]="!piece.on"
                  [attr.aria-pressed]="piece.on"
                  [attr.data-piece]="piece.piece"
                  (click)="setPiece(drums, piece.piece, { on: !piece.on })"
                >
                  {{ pieceNames[piece.piece] }}
                </button>
                <div
                  class="bezel-group text-foreground h-6 justify-self-start text-[11px]"
                  role="radiogroup"
                  [attr.aria-label]="pieceNames[piece.piece] + ' pattern'"
                >
                  @for (variation of variations; track variation) {
                    <button
                      type="button"
                      role="radio"
                      class="w-7 disabled:opacity-35"
                      [class.text-lit]="piece.variation === variation"
                      [attr.aria-checked]="piece.variation === variation"
                      [disabled]="!piece.on"
                      (click)="setPiece(drums, piece.piece, { variation })"
                    >
                      {{ variation + 1 }}
                    </button>
                  }
                </div>
              }
            </div>
            <h3 [class]="band">Follow</h3>
            <div class="grid grid-cols-[6rem_1fr] items-center gap-x-2 gap-y-1.5 px-3 py-2 text-xs">
              <label class="text-foreground-muted" for="session-player-rhythm-source">Rhythm source</label>
              <select
                id="session-player-rhythm-source"
                [class]="field"
                data-testid="session-player-rhythm-source"
                [value]="drums.rhythmSourceTrackId ?? ''"
                (change)="
                  setDrums({ ...drums, rhythmSourceTrackId: selectValue($event) || null }, 'Follow Rhythm Source')
                "
              >
                <option value="">None</option>
                @for (track of rhythmSources(); track track.id) {
                  <option [value]="track.id">{{ track.name }}</option>
                }
              </select>
            </div>
          }
          @if (r.parameters.bass; as bass) {
            <h3 [class]="band">Bass</h3>
            <div class="grid grid-cols-[6rem_1fr] items-center gap-x-2 gap-y-1.5 px-3 py-2 text-xs">
              <label class="text-foreground-muted" for="session-player-bass-low">Lowest note</label>
              <select
                id="session-player-bass-low"
                [class]="field"
                [value]="bass.lowNote"
                (change)="setBass({ ...bass, lowNote: +selectValue($event) })"
              >
                @for (choice of bassNotes; track choice.note) {
                  <option [value]="choice.note" [disabled]="choice.note > bass.highNote - 12">{{ choice.name }}</option>
                }
              </select>
              <label class="text-foreground-muted" for="session-player-bass-high">Highest note</label>
              <select
                id="session-player-bass-high"
                [class]="field"
                [value]="bass.highNote"
                (change)="setBass({ ...bass, highNote: +selectValue($event) })"
              >
                @for (choice of bassNotes; track choice.note) {
                  <option [value]="choice.note" [disabled]="choice.note < bass.lowNote + 12">{{ choice.name }}</option>
                }
              </select>
              <span class="text-foreground-muted">Articulation</span>
              <div class="flex gap-1">
                <button
                  type="button"
                  class="bezel h-6 px-2 text-[11px] font-semibold"
                  [class.text-lit]="bass.slides"
                  [class.text-foreground]="!bass.slides"
                  [attr.aria-pressed]="bass.slides"
                  (click)="setBass({ ...bass, slides: !bass.slides })"
                >
                  Slides
                </button>
                <button
                  type="button"
                  class="bezel h-6 px-2 text-[11px] font-semibold"
                  [class.text-lit]="bass.deadNotes"
                  [class.text-foreground]="!bass.deadNotes"
                  [attr.aria-pressed]="bass.deadNotes"
                  (click)="setBass({ ...bass, deadNotes: !bass.deadNotes })"
                >
                  Dead Notes
                </button>
              </div>
            </div>
          }
          @if (r.parameters.keys; as keys) {
            <h3 [class]="band">Keys</h3>
            <div class="grid grid-cols-[6rem_1fr] items-center gap-x-2 gap-y-1.5 px-3 py-2 text-xs">
              <span class="text-foreground-muted">Voicing</span>
              <div
                class="bezel-group text-foreground h-6 justify-self-start text-[11px]"
                role="radiogroup"
                aria-label="Voicing"
              >
                @for (voicing of voicings; track voicing.value) {
                  <button
                    type="button"
                    role="radio"
                    class="px-2"
                    [class.text-lit]="keys.voicing === voicing.value"
                    [attr.aria-checked]="keys.voicing === voicing.value"
                    (click)="setKeys({ ...keys, voicing: voicing.value })"
                  >
                    {{ voicing.label }}
                  </button>
                }
              </div>
              <label class="text-foreground-muted" for="session-player-keys-low">Lowest note</label>
              <select
                id="session-player-keys-low"
                [class]="field"
                [value]="keys.lowNote"
                (change)="setKeys({ ...keys, lowNote: +selectValue($event) })"
              >
                @for (choice of keyNotes; track choice.note) {
                  <option [value]="choice.note" [disabled]="choice.note > keys.highNote - 12">{{ choice.name }}</option>
                }
              </select>
              <label class="text-foreground-muted" for="session-player-keys-high">Highest note</label>
              <select
                id="session-player-keys-high"
                [class]="field"
                [value]="keys.highNote"
                (change)="setKeys({ ...keys, highNote: +selectValue($event) })"
              >
                @for (choice of keyNotes; track choice.note) {
                  <option [value]="choice.note" [disabled]="choice.note < keys.lowNote + 12">{{ choice.name }}</option>
                }
              </select>
              <label class="text-foreground-muted" for="session-player-left-hand">Left hand</label>
              <select
                id="session-player-left-hand"
                [class]="field"
                [value]="keys.leftHand"
                (change)="setLeftHand(keys, $event)"
              >
                @for (hand of leftHands; track hand.value) {
                  <option [value]="hand.value">{{ hand.label }}</option>
                }
              </select>
              <label class="text-foreground-muted" for="session-player-rhythm">Rhythm</label>
              <select
                id="session-player-rhythm"
                [class]="field"
                [value]="keys.rhythmId ?? ''"
                (change)="setKeys({ ...keys, rhythmId: selectValue($event) || null })"
              >
                <option value="">By complexity</option>
                @for (rhythm of rhythms(); track rhythm.id) {
                  <option [value]="rhythm.id">{{ rhythm.name }}</option>
                }
              </select>
            </div>
          }
        </div>
      </div>
    } @else {
      <p class="text-foreground-muted grid h-full place-items-center px-6 text-center text-xs">
        Select a Drummer, Bass Player or Keyboard Player region to edit how it plays. The Tracks area's new track menu
        adds a Session Player track, with a region for each arrangement section.
      </p>
    }
  `
})
export class SessionPlayerEditor {
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  protected readonly players = inject(SessionPlayersStore);

  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly selectValue = selectValue;
  protected readonly pieceNames = PieceNames;
  protected readonly variations = [0, 1, 2, 3];
  protected readonly voicings = Voicings;
  protected readonly leftHands = LeftHands;
  protected readonly bassNotes = noteChoices(24, 72);
  protected readonly keyNotes = noteChoices(36, 96);
  protected readonly feelText = (value: number) =>
    value === 0 ? 'On the beat' : value < 0 ? `Lay back ${Math.round(-value)} %` : `Push ${Math.round(value)} %`;

  /** The region shown: the selected Session Player region, else the selected Session Player track's first. */
  protected readonly region = computed<SessionPlayerRegion | null>(() => {
    const regions = this.project.sessionPlayerRegions();
    for (const id of this.selection.regions()) {
      const region = regions.get(id);
      if (region) {
        return region;
      }
    }
    const track = this.selection.track();
    return this.players.regions().find(r => r.trackId === track) ?? null;
  });
  protected readonly player = computed(
    () => playerOf(this.project.track(this.region()?.trackId ?? '')?.kind) ?? 'drummer'
  );
  protected readonly playerName = computed(() => PlayerNames[this.player()]);
  protected readonly styles = computed(() => this.players.styles(this.player()));
  protected readonly style = computed(
    () => this.styles().find(s => s.id === this.region()?.parameters.styleId) ?? null
  );
  protected readonly styleName = computed(() => this.style()?.name ?? '');
  protected readonly presets = computed(() => this.style()?.presets ?? []);
  protected readonly rhythms = computed(() => this.style()?.rhythms ?? []);
  /** The XY pad while it is dragged, else the region's. */
  protected readonly draft = linkedSignal<string | null, SessionPadValue | null>({
    source: () => this.region()?.id ?? null,
    computation: () => null
  });
  protected readonly pad = computed(
    () => this.draft() ?? this.region()?.parameters.pad ?? { complexity: 0.5, intensity: 0.5 }
  );
  private padSent: SessionPadValue | null = null;
  /** The track's other regions on the pad. */
  protected readonly others = computed<SessionPadMarker[]>(() => {
    const region = this.region();
    if (!region) {
      return [];
    }
    return this.players
      .regions()
      .filter(r => r.trackId === region.trackId && r.id !== region.id)
      .map(r => ({ id: r.id, name: r.name, value: r.parameters.pad }));
  });
  /** Tracks a Drummer can follow: the ones that play MIDI, not itself. */
  protected readonly rhythmSources = computed(() =>
    this.project
      .trackList()
      .filter(t => t.id !== this.region()?.trackId && t.parentId !== this.region()?.trackId)
      .filter(t => t.kind === 'instrument' || t.kind === 'midi' || t.kind === 'bassPlayer' || t.kind === 'audio')
  );

  constructor() {
    void this.players.loadCatalog();
    effect(() => {
      const draft = this.draft();
      const pad = this.region()?.parameters.pad;
      if (draft && draft.complexity === pad?.complexity && draft.intensity === pad.intensity) {
        this.padSent = null;
        this.draft.set(null);
      }
    });
  }

  protected set(changes: Parameters<SessionPlayersStore['set']>[1], label: string): Promise<unknown> {
    const region = this.region();
    return region ? this.players.set(region.id, changes, label) : Promise.resolve();
  }

  protected commitPad(value: SessionPadValue): void {
    // Against what was last sent: the region may not have heard of it yet.
    const current = this.padSent ?? this.region()?.parameters.pad;
    if (current && (current.complexity !== value.complexity || current.intensity !== value.intensity)) {
      this.padSent = value;
      // The region's answer redraws the puck (the constructor's effect clears the draft); until then the value stays,
      // so a key held down keeps stepping from it. A refused edit goes back to the region's.
      void this.set({ complexity: value.complexity, intensity: value.intensity }, 'Complexity and Intensity').then(
        result => {
          if (result === null && this.draft() === value) {
            this.padSent = null;
            this.draft.set(null);
          }
        }
      );
    } else {
      queueMicrotask(() => this.draft.set(null));
    }
  }

  protected setStyle(styleId: string): void {
    if (styleId !== this.region()?.parameters.styleId) {
      void this.set({ styleId }, 'Session Player Style');
    }
  }

  protected setPreset(presetId: string): void {
    void this.set({ presetId }, 'Session Player Preset');
  }

  protected setPiece(drums: DrummerSwitches, piece: DrumPiece, changes: { on?: boolean; variation?: number }): void {
    this.setDrums(
      { ...drums, pieces: drums.pieces.map(p => (p.piece === piece ? { ...p, ...changes } : p)) },
      changes.on === undefined ? 'Pattern Variation' : 'Kit Piece'
    );
  }

  protected setDrums(drums: DrummerSwitches, label: string): void {
    void this.set({ drums }, label);
  }

  protected setBass(bass: BassSwitches): void {
    void this.set({ bass }, 'Bass Player');
  }

  protected setLeftHand(keys: KeysSwitches, event: Event): void {
    const leftHand = LeftHands.find(hand => hand.value === selectValue(event))?.value;
    if (leftHand) {
      this.setKeys({ ...keys, leftHand });
    }
  }

  protected setKeys(keys: KeysSwitches): void {
    void this.set({ keys }, 'Keyboard Player');
  }

  protected convert(target: 'midi' | 'pattern'): void {
    const region = this.region();
    if (region) {
      void this.players.convert([region.id], target);
    }
  }
}
