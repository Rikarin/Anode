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
  matChevronRightRound,
  matExpandMoreRound,
  matFolderRound,
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
  providers: [
    provideIcons({
      matChevronRightRound,
      matExpandMoreRound,
      matFolderRound,
      matGraphicEqRound,
      matMoreHorizRound,
      matPianoRound,
      matSettingsInputComponentRound
    })
  ],
  host: {
    class: 'an-track-header border-hairline flex items-stretch gap-2 border-b pe-2',
    role: 'group',
    '[attr.aria-label]': '"Track " + number() + ": " + track().name',
    '[attr.data-track-id]': 'track().id',
    '[class.bg-muted]': 'selected()',
    '[class.bg-surface]': '!selected()',
    '[style.margin-inline-start.px]': 'depth() * 14',
    '[attr.data-stack]': 'track().kind === "stack" || null',
    '(click)': 'select($event)'
  },
  template: `
    <button
      type="button"
      class="focus-visible:outline-focus w-1.5 shrink-0 cursor-pointer shadow-[inset_-1px_0_0_var(--hairline)] focus-visible:outline-2"
      [style.background-color]="track().color"
      [attr.aria-label]="'Colour of ' + track().name"
      [xuiPopover]="palette"
      placement="right-start"
    ></button>
    <ng-template #palette>
      <div class="grid grid-cols-6 gap-1.5 p-1.5" role="group" aria-label="Track colours">
        @for (color of colors; track color) {
          <button
            type="button"
            class="focus-visible:outline-focus aria-pressed:ring-foreground size-5 rounded-[5px] shadow-[inset_0_0_0_1px_var(--hairline)] focus-visible:outline-2 aria-pressed:ring-2 aria-pressed:ring-offset-1"
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
        @if (track().kind === 'stack') {
          <button
            type="button"
            class="text-foreground-muted hover:text-foreground -ms-1 grid size-4 shrink-0 place-items-center"
            [attr.aria-label]="(track().collapsed ? 'Expand stack ' : 'Collapse stack ') + track().name"
            [attr.aria-expanded]="!track().collapsed"
            data-testid="stack-disclosure"
            (click)="$event.stopPropagation(); toggleCollapsed()"
          >
            <ng-icon xui size="sm" [name]="track().collapsed ? 'matChevronRightRound' : 'matExpandMoreRound'" />
          </button>
        }
        <span class="text-foreground-subtle w-4 shrink-0 text-end text-[11px] font-medium tabular-nums">{{
          number()
        }}</span>
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
            class="text-foreground min-w-0 flex-1 truncate text-[12px] font-semibold"
            [attr.title]="track().name + ' (double-click to rename)'"
            (dblclick)="startRename($event)"
          >
            {{ track().name }}
          </span>
        }
        @if (track().kind !== 'audio') {
          <span
            class="size-1.5 shrink-0 rounded-full transition-colors duration-100"
            [class.bg-meter-low]="active()"
            [class.bg-border-strong]="!active()"
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
            @if (track().kind === 'stack') {
              <button xuiMenuItem type="button" (click)="unstack()">Flatten Stack</button>
            } @else {
              <button xuiMenuItem type="button" (click)="stack()">Create Track Stack</button>
              @if (track().parentId) {
                <button xuiMenuItem type="button" (click)="leaveStack()">Remove from Stack</button>
              }
            }
            <xui-menu-divider />
            <button xuiMenuItem type="button" color="error" (click)="remove()">Delete Track</button>
          </xui-menu>
        </ng-template>
      </div>
      <div class="flex items-center gap-1">
        <button
          type="button"
          class="state-toggle toggle-mute"
          [attr.aria-pressed]="track().strip.mute"
          [attr.aria-label]="'Mute ' + track().name"
          xuiTooltip="Mute"
          (click)="mixer.toggleMute(track().id)"
        >
          M
        </button>
        <button
          type="button"
          class="state-toggle toggle-solo"
          [attr.aria-pressed]="track().strip.solo"
          [attr.aria-label]="'Solo ' + track().name"
          xuiTooltip="Solo"
          (click)="mixer.toggleSolo(track().id)"
        >
          S
        </button>
        <button
          type="button"
          class="state-toggle toggle-arm"
          [class.invisible]="track().kind === 'stack'"
          [disabled]="track().kind === 'stack'"
          [attr.aria-pressed]="track().recordArmed"
          [attr.aria-label]="'Record enable ' + track().name"
          xuiTooltip="Record enable"
          (click)="change({ recordArmed: !track().recordArmed })"
        >
          R
        </button>
        <button
          type="button"
          class="state-toggle toggle-monitor"
          [class.invisible]="track().kind !== 'audio'"
          [disabled]="track().kind !== 'audio'"
          [attr.aria-pressed]="track().inputMonitoring"
          [attr.aria-label]="'Input monitoring ' + track().name"
          xuiTooltip="Input monitoring"
          (click)="change({ inputMonitoring: !track().inputMonitoring })"
        >
          I
        </button>
        <label class="an-track-volume flex min-w-0 flex-1 items-center ps-1" [attr.title]="'Volume ' + volumeText()">
          <span class="sr-only">Volume of {{ track().name }}</span>
          <xui-slider
            class="min-w-0 flex-1"
            color="none"
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
  /** 1 for a track in a stack: its header is indented under the stack's. */
  readonly depth = input(0);

  protected readonly colors = TrackColors;
  protected readonly renaming = signal(false);
  private readonly nameInput = viewChild<ElementRef<HTMLInputElement>>('name');
  protected readonly number = computed(() => this.index() + 1);
  protected readonly count = computed(() => this.project.trackIds().length);
  protected readonly selected = computed(
    () => this.selection.track() === this.track().id || this.selection.tracks().has(this.track().id)
  );
  protected readonly volume = computed(() => this.mixer.value(this.track().id, 'volumeDb'));
  protected readonly volumePosition = computed(() => faderPosition(this.volume()));
  protected readonly volumeText = computed(() => formatDb(this.volume()));
  private readonly live = inject(LiveMidiStore);
  protected readonly active = computed(() => this.live.active().has(this.track().id));
  protected readonly kindIcon = computed(
    () =>
      ({
        audio: 'matGraphicEqRound',
        instrument: 'matPianoRound',
        midi: 'matSettingsInputComponentRound',
        stack: 'matFolderRound'
      })[this.track().kind]
  );
  protected readonly kindTitle = computed(() => {
    const track = this.track();
    return track.kind === 'audio'
      ? 'Audio track'
      : track.kind === 'midi'
        ? 'External MIDI track'
        : track.kind === 'stack'
          ? 'Track stack'
          : `Software instrument: ${track.instrument?.name ?? 'none'}`;
  });

  constructor() {
    effect(() => {
      const input = this.nameInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
  }

  /** Selects the track; ⇧ or ⌘ adds it to the selected tracks (to stack them). */
  protected select(event?: MouseEvent): void {
    this.selection.selectTrack(this.track().id, !!event && (event.shiftKey || event.metaKey));
  }

  protected toggleCollapsed(): void {
    const collapsed = !this.track().collapsed;
    void this.project.execute(
      [{ type: 'setTrack', trackId: this.track().id, input: null, collapsed }],
      collapsed ? 'Collapse Stack' : 'Expand Stack'
    );
  }

  /** A stack of the selected tracks (this one when it is not among them). */
  protected stack(): void {
    const selected = this.selection.trackSelection().filter(id => this.project.track(id)?.kind !== 'stack');
    const ids = selected.includes(this.track().id) ? selected : [this.track().id];
    void this.project.execute([{ type: 'createStack', trackIds: ids }], 'Create Track Stack');
  }

  protected unstack(): void {
    void this.project.execute([{ type: 'removeStack', stackId: this.track().id }], 'Flatten Stack');
  }

  protected leaveStack(): void {
    void this.project.execute(
      [{ type: 'setTrackParent', trackId: this.track().id, parentId: null }],
      'Remove from Stack'
    );
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
