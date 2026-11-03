import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { type ChannelFormat, SpeakerLayouts, type Track } from '@anode/api-client';
import {
  AnObjectPanner,
  AnSurroundPanner,
  type ObjectPannerValue,
  type SurroundPannerValue
} from '@anode/audio-ui/lazy';
import { XuiMenuImports } from '@xui/menu';
import { XuiTooltipImports } from '@xui/tooltip';
import { FloatingWindow } from '../shell/floating-window';
import { MixerStore, type PannerChange } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';

/** Whether a format is wider than stereo (`SpeakerLayouts.IsSurround`). */
export function isSurround(format: ChannelFormat): boolean {
  return SpeakerLayouts[format].speakers.length > 2;
}

/** The input formats a strip offers: audio tracks and auxes every layout; other strips are stereo. */
export const InputFormats = Object.values(SpeakerLayouts).map(l => ({ format: l.format, name: l.name }));

/** The Surround Panner's fields as `setPanner`'s flat ones. */
export function surroundChange(change: Partial<SurroundPannerValue>): PannerChange {
  const out: PannerChange = {};
  if (change.angle !== undefined) out.angle = change.angle;
  if (change.diversity !== undefined) out.diversity = change.diversity;
  if (change.elevation !== undefined) out.surroundElevation = change.elevation;
  if (change.centreLevelDb !== undefined) out.centreLevelDb = change.centreLevelDb;
  if (change.lfeLevelDb !== undefined) out.lfeLevelDb = change.lfeLevelDb;
  if (change.spread !== undefined) out.spread = change.spread;
  return out;
}

/** The 3D Object Panner's fields as `setPanner`'s flat ones. */
export function objectChange(change: Partial<ObjectPannerValue>): PannerChange {
  const out: PannerChange = {};
  if (change.azimuth !== undefined) out.azimuth = change.azimuth;
  if (change.elevation !== undefined) out.objectElevation = change.elevation;
  if (change.distance !== undefined) out.distance = change.distance;
  if (change.size !== undefined) out.size = change.size;
  return out;
}

/**
 * A surround strip's panner section (docs/11-phase-10.md), in place of the pan knob when the strip's output is
 * surround or the strip is an object: its formats (the input format as a `strip-slot` popup for audio tracks and
 * auxes, → the output's), the compact Surround Panner (an object shows its top view the same way), and the Bed/Object
 * switch (an object needs the master of a surround project). The compact panner's button opens the full editor in a
 * floating window: the Surround Panner with its elevation, centre, LFE and spread knobs, or the 3D Object Panner.
 * Moves go out as flat `setPanner` fields, coalesced (MixerStore).
 */
@Component({
  selector: 'an-strip-panner',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnObjectPanner, AnSurroundPanner, FloatingWindow, XuiMenuImports, XuiTooltipImports],
  host: { class: 'flex w-full flex-col items-center gap-1 px-1', '[attr.data-testid]': '"strip-panner"' },
  template: `
    @let t = track();
    @let p = panner();
    <button
      type="button"
      class="strip-slot text-foreground w-full justify-center gap-0.5 text-[10px]"
      data-testid="input-format"
      [attr.aria-label]="'Input format of ' + t.name + ': ' + inputName() + ', output ' + outputName()"
      [attr.disabled]="formatEditable() ? null : ''"
      [xuiTooltip]="formatEditable() ? 'Input format' : t.name + ' plays in stereo'"
      [xuiMenuTriggerFor]="formatMenu"
    >
      <span class="font-semibold">{{ inputName() }}</span>
      <span class="text-foreground-muted" aria-hidden="true">→</span>
      <span class="font-semibold">{{ outputName() }}</span>
    </button>
    @if (p) {
      @if (p.mode === 'object') {
        <an-surround-panner
          compact
          size="56"
          testId="strip-object-panner"
          [label]="t.name + ' 3D Object Panner'"
          [speakers]="masterSpeakers()"
          [value]="objectAsPuck()"
          (changed)="moveObjectPuck($event)"
          (committed)="release()"
          (expand)="editorOpen.set(true)"
        />
      } @else {
        <an-surround-panner
          compact
          size="56"
          testId="strip-surround-panner"
          [label]="t.name + ' Surround Panner'"
          [speakers]="speakers()"
          [stereoInput]="stereoInput()"
          [value]="surroundValue()"
          (changed)="moveSurround($event)"
          (committed)="release()"
          (expand)="editorOpen.set(true)"
        />
      }
      <div class="bezel-group text-foreground h-5 text-[10px] font-semibold" role="group" aria-label="Bed or object">
        <button
          type="button"
          class="px-1.5"
          data-testid="mode-bed"
          [attr.aria-pressed]="p.mode !== 'object'"
          [class.text-lit]="p.mode !== 'object'"
          [attr.disabled]="objectAllowed() ? null : ''"
          [xuiTooltip]="objectAllowed() ? 'A bed channel: the Surround Panner' : objectWhy()"
          (click)="setMode('surround')"
        >
          Bed
        </button>
        <button
          type="button"
          class="px-1.5"
          data-testid="mode-object"
          [attr.aria-pressed]="p.mode === 'object'"
          [class.text-lit]="p.mode === 'object'"
          [attr.disabled]="objectAllowed() ? null : ''"
          [xuiTooltip]="objectAllowed() ? 'An object: the 3D Object Panner' : objectWhy()"
          (click)="setMode('object')"
        >
          Obj
        </button>
      </div>
      @if (editorOpen()) {
        <an-floating-window
          [title]="t.name + (p.mode === 'object' ? ' — 3D Object Panner' : ' — Surround Panner')"
          testId="panner-window"
          width="auto"
          [origin]="{ x: 240, y: 120 }"
          (closed)="editorOpen.set(false)"
        >
          <div class="flex flex-col items-center gap-2 p-3">
            @if (p.mode === 'object') {
              <an-object-panner
                [label]="t.name + ' object'"
                [speakers]="masterSpeakers()"
                [value]="p.object"
                (changed)="moveObject($event)"
                (committed)="release()"
              />
            } @else {
              <an-surround-panner
                size="200"
                [label]="t.name + ' Surround Panner (editor)'"
                [speakers]="speakers()"
                [stereoInput]="stereoInput()"
                [value]="surroundValue()"
                (changed)="moveSurround($event)"
                (committed)="release()"
              />
            }
          </div>
        </an-floating-window>
      }
    }
    <ng-template #formatMenu>
      <xui-menu class="min-w-32" [attr.aria-label]="'Input format menu of ' + t.name">
        @for (option of formats; track option.format) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.data-format]="option.format"
            [attr.aria-checked]="t.strip.inputFormat === option.format"
            (click)="setInputFormat(option.format)"
          >
            {{ option.name }}
          </button>
        }
      </xui-menu>
    </ng-template>
  `
})
export class StripPanner {
  private readonly mixer = inject(MixerStore);
  private readonly project = inject(ProjectStore);
  readonly track = input.required<Track>();

  protected readonly formats = InputFormats;
  protected readonly editorOpen = signal(false);
  protected readonly panner = computed(() => this.mixer.panner(this.track().id));
  protected readonly speakers = computed(() => SpeakerLayouts[this.track().strip.outputFormat].speakers);
  protected readonly masterSpeakers = computed(() => SpeakerLayouts[this.project.settings().surroundFormat].speakers);
  protected readonly inputName = computed(() => SpeakerLayouts[this.track().strip.inputFormat].name);
  protected readonly outputName = computed(() => SpeakerLayouts[this.track().strip.outputFormat].name);
  protected readonly stereoInput = computed(() => this.track().strip.inputFormat === 'stereo');
  protected readonly formatEditable = computed(() => this.track().kind === 'audio' || this.track().kind === 'aux');
  protected readonly objectAllowed = computed(
    () => this.track().strip.output === 'master' && isSurround(this.project.settings().surroundFormat)
  );
  protected readonly objectWhy = computed(() =>
    isSurround(this.project.settings().surroundFormat)
      ? 'Objects go to the master: route the track to the Stereo Out first'
      : 'Objects need a surround project (File ▸ Project Settings ▸ Surround…)'
  );
  protected readonly surroundValue = computed<SurroundPannerValue>(() => {
    const s = this.panner()?.surround;
    return s ? { ...s } : { angle: 0, diversity: 1, elevation: 0, centreLevelDb: 0, lfeLevelDb: -144, spread: 60 };
  });
  /** An object's top view on the compact panner: azimuth as the angle, distance as the diversity. */
  protected readonly objectAsPuck = computed<SurroundPannerValue>(() => {
    const o = this.panner()?.object;
    return { ...this.surroundValue(), angle: o?.azimuth ?? 0, diversity: o?.distance ?? 1 };
  });

  protected moveSurround(change: Partial<SurroundPannerValue>): void {
    void this.mixer.setPanner(this.track().id, surroundChange(change));
  }

  protected moveObject(change: Partial<ObjectPannerValue>): void {
    void this.mixer.setPanner(this.track().id, objectChange(change));
  }

  protected moveObjectPuck(change: Partial<SurroundPannerValue>): void {
    this.moveObject({
      ...(change.angle === undefined ? {} : { azimuth: change.angle }),
      ...(change.diversity === undefined ? {} : { distance: change.diversity })
    });
  }

  protected release(): void {
    this.mixer.releasePanner(this.track().id);
  }

  protected setMode(mode: 'surround' | 'object'): void {
    if (this.panner()?.mode !== mode) {
      void this.mixer.setPannerMode(this.track().id, mode);
    }
  }

  protected setInputFormat(format: ChannelFormat): void {
    if (this.track().strip.inputFormat !== format) {
      void this.mixer.setInputFormat(this.track().id, format);
    }
  }
}
