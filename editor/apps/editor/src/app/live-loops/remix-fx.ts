import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { Insert } from '@anode/api-client';
import { AnXyPad, type XyValue } from '@anode/audio-ui';
import { provideIcons } from '@ng-icons/core';
import { matExpandMoreRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { MixerStore } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';

/**
 * Remix FX's plugin id and parameter ids (the fake Server's, `core/fake/fake-plugins-phase6.ts`), in one place so
 * integration can align them with the engine's (workstream D). Each pad has x and y (0…1), an effect choice (an index
 * into `effects`) and an on switch (1 while the pad is held).
 */
export const RemixFxParams = {
  pluginId: 'anode.remix-fx',
  pads: [
    { x: 'xy1X', y: 'xy1Y', mode: 'xy1Effect', on: 'xy1Active', effects: ['Filter', 'Wobble', 'Orbit'] },
    {
      x: 'xy2X',
      y: 'xy2Y',
      mode: 'xy2Effect',
      on: 'xy2Active',
      effects: ['Repeater', 'Reverse', 'Scratch', 'Gater', 'Downsampler', 'Tape Stop']
    }
  ]
} as const;

type Pad = (typeof RemixFxParams.pads)[number];

/** What each effect's axes control, for the pad's readout. */
const Axes: Record<string, [string, string]> = {
  Filter: ['Cutoff', 'Resonance'],
  Wobble: ['Rate', 'Depth'],
  Orbit: ['Rate', 'Depth'],
  Repeater: ['Rate', 'Mix'],
  Reverse: ['Length', 'Mix'],
  Scratch: ['Rate', 'Depth'],
  Gater: ['Rate', 'Depth'],
  Downsampler: ['Rate', 'Bits'],
  'Tape Stop': ['Speed', 'Mix']
};

/**
 * Remix FX under the Live Loops grid (Logic's): two XY pads on the master's Remix FX insert, pad 1 a filter, wobble or
 * orbit, pad 2 a repeater, reverse, scratch, gater, downsampler or tape stop. A pad plays its effect while held
 * (`xyN.on`); moving it sends x and y, coalesced, so a performance recording takes the moves as automation. When the
 * master has no Remix FX (the Server adds it when the grid is first shown), a button adds it.
 */
@Component({
  selector: 'an-remix-fx',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnXyPad, XuiButtonImports, XuiIconImports, XuiMenuImports],
  providers: [provideIcons({ matExpandMoreRound })],
  host: {
    class: 'border-hairline bg-surface block border-t',
    role: 'group',
    'aria-label': 'Remix FX',
    'data-testid': 'remix-fx'
  },
  template: `
    <div class="text-foreground-muted px-2 pt-1.5 text-[10px] font-semibold tracking-wide uppercase">Remix FX</div>
    @if (insert(); as fx) {
      <div class="flex items-start justify-around gap-2 px-2 pt-1 pb-2">
        @for (pad of pads; track pad.x; let i = $index) {
          <div class="flex flex-col items-center gap-1">
            <button
              type="button"
              class="bezel text-foreground flex h-6 items-center gap-1 px-2 text-[11px] font-semibold"
              [attr.aria-label]="'Pad ' + (i + 1) + ' effect'"
              [attr.data-testid]="'remix-pad-' + (i + 1) + '-effect'"
              [xuiMenuTriggerFor]="effectMenu"
              [xuiMenuTriggerData]="{ $implicit: pad }"
            >
              {{ effect(fx, pad) }}
              <ng-icon xui name="matExpandMoreRound" size="14px" />
            </button>
            <an-xy-pad
              [attr.data-testid]="'remix-pad-' + (i + 1)"
              [label]="'Pad ' + (i + 1) + ': ' + effect(fx, pad)"
              [xLabel]="axes(fx, pad)[0]"
              [yLabel]="axes(fx, pad)[1]"
              [size]="112"
              [value]="value(fx, pad)"
              (valueChange)="move(fx, pad, $event)"
              (engaged)="engage(fx, pad, $event)"
            />
          </div>
        }
      </div>
      <ng-template #effectMenu let-pad>
        <xui-menu>
          @for (name of effectsOf(pad); track name; let n = $index) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="mode(fx, pad) === n"
              (click)="setEffect(fx, pad, n)"
            >
              {{ name }}
            </button>
          }
        </xui-menu>
      </ng-template>
    } @else {
      <div class="flex items-center gap-2 px-2 pt-1 pb-2">
        <p class="text-foreground-muted flex-1 text-xs">Remix FX is not on the master.</p>
        <button xuiButton type="button" size="sm" data-testid="add-remix-fx" (click)="add()">Add Remix FX</button>
      </div>
    }
  `
})
export class RemixFx {
  private readonly project = inject(ProjectStore);
  private readonly mixer = inject(MixerStore);

  protected readonly pads = RemixFxParams.pads;

  /** The master's Remix FX insert (the first, if there are several). */
  readonly insert = computed(() => this.project.master().inserts.find(i => i.pluginId === RemixFxParams.pluginId));

  protected value(fx: Insert, pad: Pad): XyValue {
    return { x: this.mixer.parameterValue(null, fx, pad.x), y: this.mixer.parameterValue(null, fx, pad.y) };
  }

  protected mode(fx: Insert, pad: Pad): number {
    return Math.round(this.mixer.parameterValue(null, fx, pad.mode));
  }

  protected effectsOf(pad: Pad): readonly string[] {
    return pad.effects;
  }

  protected effect(fx: Insert, pad: Pad): string {
    return pad.effects[this.mode(fx, pad)] ?? pad.effects[0];
  }

  protected axes(fx: Insert, pad: Pad): [string, string] {
    return Axes[this.effect(fx, pad)] ?? ['X', 'Y'];
  }

  /** A pad moved: x and y to the master's insert (coalesced, as any parameter drag). */
  move(fx: Insert, pad: Pad, value: XyValue): void {
    const current = this.value(fx, pad);
    if (value.x !== current.x) {
      void this.mixer.setParameter(null, fx.id, pad.x, value.x);
    }
    if (value.y !== current.y) {
      void this.mixer.setParameter(null, fx.id, pad.y, value.y);
    }
  }

  /** A pad held or let go: its effect plays only while held. */
  engage(fx: Insert, pad: Pad, on: boolean): void {
    void this.mixer.setParameter(null, fx.id, pad.on, on ? 1 : 0);
  }

  setEffect(fx: Insert, pad: Pad, index: number): void {
    void this.mixer.setParameter(null, fx.id, pad.mode, index);
  }

  protected add(): void {
    void this.mixer.addInsert(null, RemixFxParams.pluginId);
  }
}
