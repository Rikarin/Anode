import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
  type ElementRef
} from '@angular/core';
import type { Track } from '@anode/api-client';
import { faderDb, faderPosition, formatDb } from '@anode/audio-ui';
import { provideIcons } from '@ng-icons/core';
import {
  matGraphicEqRound,
  matMoreHorizRound,
  matPianoRound,
  matSettingsInputComponentRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiMenuImports } from '@xui/menu';
import { XuiPopoverImports } from '@xui/popover';
import { XuiSliderImports } from '@xui/slider';
import { XuiTooltipImports } from '@xui/tooltip';
import { LiveMidiStore } from '../state/live-midi.store';
import { MixerStore } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TrackColors } from '../state/track-colors';
import { setTrack, setTrackLabel, type TrackChange } from '../state/track-commands';

/**
 * A track's header in the Tracks area (Logic's track header): its colour, number, kind and name (double-click to
 * rename), mute, solo, record enable and (audio) input monitoring, and a volume slider on the fader's dB taper. An
 * instrument or MIDI track has an activity dot that lights while the engine plays or receives MIDI on it. Its menu
 * moves and deletes the track. Every change is one command batch with a label for the undo history.
 */
@Component({
  selector: 'an-track-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    XuiButtonImports,
    XuiIconImports,
    XuiInputImports,
    XuiMenuImports,
    XuiPopoverImports,
    XuiSliderImports,
    XuiTooltipImports
  ],
  providers: [provideIcons({ matGraphicEqRound, matMoreHorizRound, matPianoRound, matSettingsInputComponentRound })],
  host: {
    class: 'border-border flex items-stretch gap-2 border-b pe-2',
    role: 'group',
    '[attr.aria-label]': '"Track " + number() + ": " + track().name',
    '[attr.data-track-id]': 'track().id',
    '[class.bg-surface-raised]': 'selected()',
    '[class.bg-surface]': '!selected()',
    '(click)': 'select()'
  },
  template: `
    <button
      type="button"
      class="focus-visible:outline-focus w-1.5 shrink-0 cursor-pointer focus-visible:outline-2"
      [style.background-color]="track().color"
      [attr.aria-label]="'Colour of ' + track().name"
      [xuiPopover]="palette"
      placement="right-start"
    ></button>
    <ng-template #palette>
      <div class="grid grid-cols-4 gap-1 p-1" role="group" aria-label="Track colours">
        @for (color of colors; track color) {
          <button
            type="button"
            class="focus-visible:outline-focus size-5 rounded-sm focus-visible:outline-2"
            [style.background-color]="color"
            [attr.aria-label]="'Colour ' + color"
            [attr.aria-pressed]="color === track().color"
            (click)="change({ color })"
          ></button>
        }
      </div>
    </ng-template>
    <div class="flex min-w-0 flex-1 flex-col justify-center gap-1 py-1">
      <div class="flex min-w-0 items-center gap-1.5">
        <span class="text-foreground-muted w-4 shrink-0 text-end text-[11px] tabular-nums">{{ number() }}</span>
        <ng-icon
          xui
          size="sm"
          class="text-foreground-muted shrink-0"
          [name]="kindIcon()"
          [attr.title]="kindTitle()"
          aria-hidden="true"
        />
        @if (renaming()) {
          <input
            #name
            xuiInput
            class="h-6 min-w-0 flex-1 px-1 text-xs"
            [value]="track().name"
            [attr.aria-label]="'Name of track ' + number()"
            (keydown.enter)="rename(name.value)"
            (keydown.escape)="renaming.set(false)"
            (blur)="rename(name.value)"
          />
        } @else {
          <span
            class="text-foreground min-w-0 flex-1 truncate text-xs font-medium"
            [attr.title]="track().name + ' (double-click to rename)'"
            (dblclick)="startRename($event)"
          >
            {{ track().name }}
          </span>
        }
        @if (track().kind !== 'audio') {
          <span
            class="size-1.5 shrink-0 rounded-full transition-colors"
            [class.bg-success]="active()"
            [class.bg-border]="!active()"
            role="img"
            data-testid="midi-activity"
            [attr.data-active]="active() || null"
            [attr.aria-label]="active() ? 'MIDI activity' : 'No MIDI activity'"
          ></span>
        }
        <button
          xuiButton
          type="button"
          variant="ghost"
          size="sm"
          class="h-5 w-5 px-0"
          [attr.aria-label]="'More for ' + track().name"
          [xuiMenuTriggerFor]="menu"
        >
          <ng-icon xui name="matMoreHorizRound" size="sm" />
        </button>
        <ng-template #menu>
          <xui-menu>
            <button xuiMenuItem type="button" (click)="startRename()">Rename</button>
            <button xuiMenuItem type="button" [disabled]="index() === 0" (click)="moveBy(-1)">Move Up</button>
            <button xuiMenuItem type="button" [disabled]="index() === count() - 1" (click)="moveBy(1)">
              Move Down
            </button>
            <xui-menu-divider />
            <button xuiMenuItem type="button" color="error" (click)="remove()">Delete Track</button>
          </xui-menu>
        </ng-template>
      </div>
      <div class="flex items-center gap-1">
        <button
          xuiButton
          type="button"
          size="sm"
          [class]="toggleClass"
          [variant]="track().strip.mute ? 'default' : 'outline'"
          [color]="track().strip.mute ? 'info' : 'secondary'"
          [active]="track().strip.mute"
          [attr.aria-label]="'Mute ' + track().name"
          xuiTooltip="Mute"
          (click)="mixer.toggleMute(track().id)"
        >
          M
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          [class]="toggleClass"
          [variant]="track().strip.solo ? 'default' : 'outline'"
          [color]="track().strip.solo ? 'warning' : 'secondary'"
          [active]="track().strip.solo"
          [attr.aria-label]="'Solo ' + track().name"
          xuiTooltip="Solo"
          (click)="mixer.toggleSolo(track().id)"
        >
          S
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          [class]="toggleClass"
          [variant]="track().recordArmed ? 'default' : 'outline'"
          [color]="track().recordArmed ? 'error' : 'secondary'"
          [active]="track().recordArmed"
          [attr.aria-label]="'Record enable ' + track().name"
          xuiTooltip="Record enable"
          (click)="change({ recordArmed: !track().recordArmed })"
        >
          R
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          [class]="toggleClass"
          [class.invisible]="track().kind !== 'audio'"
          [disabled]="track().kind !== 'audio'"
          [variant]="track().inputMonitoring ? 'default' : 'outline'"
          [color]="track().inputMonitoring ? 'warning' : 'secondary'"
          [active]="track().inputMonitoring"
          [attr.aria-label]="'Input monitoring ' + track().name"
          xuiTooltip="Input monitoring"
          (click)="change({ inputMonitoring: !track().inputMonitoring })"
        >
          I
        </button>
        <label class="flex min-w-0 flex-1 items-center" [attr.title]="'Volume ' + volumeText()">
          <span class="sr-only">Volume of {{ track().name }}</span>
          <xui-slider
            class="min-w-0 flex-1"
            [min]="0"
            [max]="1"
            [stepSize]="0.001"
            [labelRenderer]="false"
            [value]="volumePosition()"
            (valueChange)="setVolume($event)"
          />
        </label>
      </div>
    </div>
  `
})
export class TrackHeader {
  protected readonly mixer = inject(MixerStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);

  readonly track = input.required<Track>();
  /** Position in the track list, from 0. */
  readonly index = input.required<number>();

  protected readonly colors = TrackColors;
  protected readonly toggleClass = 'h-5 w-5 px-0 text-[10px] font-semibold';
  protected readonly renaming = signal(false);
  private readonly nameInput = viewChild<ElementRef<HTMLInputElement>>('name');
  protected readonly number = computed(() => this.index() + 1);
  protected readonly count = computed(() => this.project.trackIds().length);
  protected readonly selected = computed(() => this.selection.track() === this.track().id);
  protected readonly volume = computed(() => this.mixer.value(this.track().id, 'volumeDb'));
  protected readonly volumePosition = computed(() => faderPosition(this.volume()));
  protected readonly volumeText = computed(() => formatDb(this.volume()));
  private readonly live = inject(LiveMidiStore);
  protected readonly active = computed(() => this.live.active().has(this.track().id));
  protected readonly kindIcon = computed(
    () =>
      ({ audio: 'matGraphicEqRound', instrument: 'matPianoRound', midi: 'matSettingsInputComponentRound' })[
        this.track().kind
      ]
  );
  protected readonly kindTitle = computed(() => {
    const track = this.track();
    return track.kind === 'audio'
      ? 'Audio track'
      : track.kind === 'midi'
        ? 'External MIDI track'
        : `Software instrument: ${track.instrument?.name ?? 'none'}`;
  });

  constructor() {
    effect(() => {
      const input = this.nameInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
  }

  protected select(): void {
    this.selection.track.set(this.track().id);
  }

  protected startRename(event?: Event): void {
    event?.stopPropagation();
    this.renaming.set(true);
  }

  protected rename(name: string): void {
    if (!this.renaming()) {
      return;
    }

    this.renaming.set(false);
    const trimmed = name.trim();
    if (trimmed && trimmed !== this.track().name) {
      this.change({ name: trimmed });
    }
  }

  protected change(change: TrackChange): void {
    void this.project.execute([setTrack(this.track().id, change)], setTrackLabel(change));
  }

  protected setVolume(position: number): void {
    void this.mixer.set(this.track().id, 'volumeDb', Math.round(faderDb(position) * 10) / 10);
  }

  protected moveBy(offset: number): void {
    const index = this.index() + offset;
    if (index >= 0 && index < this.count()) {
      void this.project.execute([{ type: 'moveTrack', trackId: this.track().id, index }], 'Move Track');
    }
  }

  protected remove(): void {
    void this.project.execute([{ type: 'removeTrack', trackId: this.track().id }], 'Delete Track');
  }
}
