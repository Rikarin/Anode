import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { type ChannelFormat, SpeakerLayouts } from '@anode/api-client';
import { speakerPoint } from '@anode/audio-ui/lazy';
import { Band } from '../control/styles';
import { FloatingWindow } from '../shell/floating-window';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';

/** A layout's speakers as a small top-view diagram (ahead up; the upper layer smaller and inside; the LFE apart). */
@Component({
  selector: 'an-speaker-diagram',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-block size-9 shrink-0', 'aria-hidden': 'true' },
  template: `
    <svg viewBox="-1.25 -1.25 2.5 2.5" class="size-full">
      <circle cx="0" cy="0" r="1" class="stroke-knob-track fill-none" stroke-width="0.06" />
      @for (p of points(); track $index) {
        <circle
          [attr.cx]="p.x"
          [attr.cy]="-p.y"
          [attr.r]="p.upper ? 0.13 : 0.17"
          [class]="p.upper ? 'fill-foreground-muted' : 'fill-foreground'"
        />
      }
      @if (lfe()) {
        <rect x="0.8" y="0.85" width="0.35" height="0.3" rx="0.06" class="fill-foreground-muted" />
      }
    </svg>
  `
})
export class SpeakerDiagram {
  readonly format = input.required<ChannelFormat>();
  protected readonly points = computed(() =>
    SpeakerLayouts[this.format()].speakers.flatMap(s => {
      const point = speakerPoint(s);
      return point ? [{ ...point, upper: s.elevation > 0 }] : [];
    })
  );
  protected readonly lfe = computed(() => SpeakerLayouts[this.format()].speakers.some(s => s.lfe));
}

/** The formats in the order the window lists them: mono and stereo, then wider, then immersive. */
export const ProjectFormats = Object.values(SpeakerLayouts);

/**
 * File ▸ Project Settings ▸ Surround… (docs/11-phase-10.md, ADR-0012): the project's surround format, the master's
 * channel format, as a list of the layouts with their speakers, channel counts and ITU-R BS.2051 names. A change is a
 * `setSurroundFormat` edit (one undo step); every strip's output format follows its destination, so tracks to the
 * Stereo Out pan with the Surround Panner once the project is wider than stereo. Lazy-loaded.
 */
@Component({
  selector: 'an-surround-settings-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow, SpeakerDiagram],
  template: `
    <an-floating-window
      title="Surround"
      testId="surround-settings"
      width="26rem"
      [origin]="{ x: 240, y: 96 }"
      (closed)="ui.surroundSettingsOpen.set(false)"
    >
      <h3 [class]="band">Project Format</h3>
      <div class="flex flex-col gap-0.5 p-2 text-xs" role="radiogroup" aria-label="Project surround format">
        @for (layout of formats; track layout.format) {
          <button
            type="button"
            role="radio"
            class="hover:bg-surface-raised focus-visible:outline-focus flex h-11 items-center gap-3 rounded-md px-2 text-start focus-visible:outline-2"
            [class.bg-primary/25]="current() === layout.format"
            [attr.aria-checked]="current() === layout.format"
            [attr.data-format]="layout.format"
            (click)="choose(layout.format)"
          >
            <an-speaker-diagram [format]="layout.format" />
            <span class="text-foreground w-12 text-[13px] font-semibold">{{ layout.name }}</span>
            <span class="text-foreground-muted readout flex-1">
              {{ layout.speakers.length }} channels{{ layout.ituName ? ' · BS.2051 ' + layout.ituName : '' }}
            </span>
          </button>
        }
      </div>
      <p class="text-foreground-muted border-hairline border-t px-3 py-2 text-[11px]" data-testid="surround-note">
        The Stereo Out and every strip to it take this format: tracks pan with the Surround Panner, or as objects with
        the 3D Object Panner. Buses keep the format of their aux. Monitoring (binaural, down-mix) is in Settings.
      </p>
    </an-floating-window>
  `
})
export class SurroundSettingsWindow {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);
  protected readonly band = Band;
  protected readonly formats = ProjectFormats;
  protected readonly current = computed(() => this.project.settings().surroundFormat);

  protected choose(format: ChannelFormat): void {
    if (format !== this.current()) {
      void this.project.execute(
        [{ type: 'setSurroundFormat', format }],
        `Project Format ${SpeakerLayouts[format].name}`
      );
    }
  }
}
