import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { Cell } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matCloseRound, matExpandMoreRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiMenuImports } from '@xui/menu';
import { Ppq } from '../midi/midi-math';
import { ProjectStore } from '../state/project.store';
import { CellPlayModes, barTicks, lengthLabel, playModeLabel } from './grid-model';
import { LaunchQuantizes, type Quantize, quantizeLabel } from './launcher';
import { LiveLoopsStore } from './live-loops.store';

/** A MIDI cell's notes as bars in a 0…100 × 0…40 box (the miniature). */
function miniature(cell: Cell): { x: number; y: number; w: number }[] {
  const notes = cell.content.midi?.notes ?? [];
  if (!notes.length || cell.lengthTicks <= 0) {
    return [];
  }
  const low = Math.min(...notes.map(n => n.pitch));
  const high = Math.max(...notes.map(n => n.pitch));
  const span = Math.max(12, high - low + 1);
  return notes.map(n => ({
    x: (n.startTick / cell.lengthTicks) * 100,
    y: 38 - ((n.pitch - low) / span) * 36,
    w: Math.max(0.8, (n.lengthTicks / cell.lengthTicks) * 100)
  }));
}

/**
 * The cell editor under the Live Loops grid (Logic's cell inspector): the selected cell's name, length in bars and
 * beats, play mode and launch quantise (its own, or the grid's), and what it plays: a MIDI cell's notes as a
 * miniature, a pattern's rows, an audio cell's file.
 */
@Component({
  selector: 'an-cell-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiIconImports, XuiInputImports, XuiMenuImports],
  providers: [provideIcons({ matCloseRound, matExpandMoreRound })],
  host: {
    class: 'border-hairline bg-surface block border-t',
    role: 'group',
    'aria-label': 'Cell editor',
    'data-testid': 'cell-editor'
  },
  template: `
    @if (live.current(); as cell) {
      <div class="border-hairline bg-surface-raised flex items-center border-b px-2 py-0.5">
        <span class="text-foreground flex-1 text-[11px] font-bold">Cell</span>
        <button
          type="button"
          class="text-foreground-muted hover:text-foreground grid size-5 place-items-center"
          aria-label="Close the cell editor"
          (click)="live.clearSelection()"
        >
          <ng-icon xui name="matCloseRound" size="14px" />
        </button>
      </div>
      <div class="grid grid-cols-[4.5rem_1fr] items-center gap-x-2 gap-y-1 px-2 py-1.5 text-[11px]">
        <label class="text-foreground-muted" for="an-cell-name">Name</label>
        <input
          #name
          id="an-cell-name"
          xuiInput
          class="h-6 min-w-0 px-1 text-xs"
          [value]="cell.name"
          (keydown.enter)="name.blur()"
          (blur)="rename(cell, name.value)"
        />
        <span class="text-foreground-muted" id="an-cell-length">Length</span>
        <div
          class="flex items-center gap-1"
          role="group"
          aria-labelledby="an-cell-length"
          [attr.title]="lengthText(cell)"
        >
          <input
            #bars
            xuiInput
            type="number"
            min="0"
            max="64"
            class="readout h-6 w-11 px-1 text-xs"
            aria-label="Cell length, bars"
            [value]="barsOf(cell)"
            (change)="setLength(cell, bars.valueAsNumber, beats.valueAsNumber)"
          />
          <span class="text-foreground-muted">bars</span>
          <input
            #beats
            xuiInput
            type="number"
            min="0"
            max="15"
            class="readout h-6 w-11 px-1 text-xs"
            aria-label="Cell length, beats"
            [value]="beatsOf(cell)"
            (change)="setLength(cell, bars.valueAsNumber, beats.valueAsNumber)"
          />
          <span class="text-foreground-muted">beats</span>
        </div>
        <span class="text-foreground-muted">Play Mode</span>
        <button
          type="button"
          class="bezel text-foreground flex h-6 items-center justify-between gap-1 px-2 font-semibold"
          aria-label="Cell play mode"
          [xuiMenuTriggerFor]="modeMenu"
        >
          {{ modeLabel(cell) }}
          <ng-icon xui name="matExpandMoreRound" size="14px" />
        </button>
        <span class="text-foreground-muted">Quantize</span>
        <button
          type="button"
          class="bezel text-foreground flex h-6 items-center justify-between gap-1 px-2 font-semibold"
          aria-label="Cell quantize"
          [xuiMenuTriggerFor]="quantizeMenu"
        >
          {{ quantizeText(cell) }}
          <ng-icon xui name="matExpandMoreRound" size="14px" />
        </button>
        <span class="text-foreground-muted">Content</span>
        @switch (cell.kind) {
          @case ('midi') {
            <svg
              viewBox="0 0 100 40"
              preserveAspectRatio="none"
              class="well h-8 w-full rounded-[4px]"
              role="img"
              [attr.aria-label]="(cell.content.midi?.notes?.length ?? 0) + ' notes'"
            >
              @for (note of notes(); track $index) {
                <rect [attr.x]="note.x" [attr.y]="note.y" [attr.width]="note.w" height="2" class="fill-primary" />
              }
            </svg>
          }
          @case ('pattern') {
            <span class="text-foreground readout">
              {{ cell.content.pattern?.pattern?.rows?.length ?? 0 }} rows ×
              {{ cell.content.pattern?.pattern?.rows?.[0]?.length ?? 0 }} steps
            </span>
          }
          @default {
            <span class="text-foreground truncate">{{ cell.content.audio?.name ?? 'Audio' }}</span>
          }
        }
      </div>
      <ng-template #modeMenu>
        <xui-menu>
          @for (mode of playModes; track mode.value) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="cell.playMode === mode.value"
              (click)="live.setPlayMode([cell], mode.value)"
            >
              {{ mode.label }}
            </button>
          }
        </xui-menu>
      </ng-template>
      <ng-template #quantizeMenu>
        <xui-menu>
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.aria-checked]="!cell.quantize"
            (click)="live.setCellQuantize([cell], null)"
          >
            Grid ({{ gridQuantize() }})
          </button>
          <xui-menu-divider />
          @for (q of quantizes; track q.value) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="cell.quantize === q.value"
              (click)="setQuantize(cell, q.value)"
            >
              {{ q.label }}
            </button>
          }
        </xui-menu>
      </ng-template>
    }
  `
})
export class CellEditor {
  protected readonly live = inject(LiveLoopsStore);
  private readonly project = inject(ProjectStore);

  protected readonly playModes = CellPlayModes;
  protected readonly quantizes = LaunchQuantizes;
  protected readonly gridQuantize = computed(() => quantizeLabel(this.project.liveLoops().quantize));
  protected readonly notes = computed(() => {
    const cell = this.live.current();
    return cell ? miniature(cell) : [];
  });
  private readonly beatTicks = computed(() => (Ppq * 4) / this.project.settings().signature.denominator);
  private readonly barLength = computed(() => barTicks(this.project.meter()));

  protected barsOf(cell: Cell): number {
    return Math.floor(cell.lengthTicks / this.barLength() + 1e-9);
  }

  protected beatsOf(cell: Cell): number {
    return Math.round((cell.lengthTicks - this.barsOf(cell) * this.barLength()) / this.beatTicks());
  }

  protected modeLabel(cell: Cell): string {
    return playModeLabel(cell.playMode);
  }

  protected quantizeText(cell: Cell): string {
    return cell.quantize ? quantizeLabel(cell.quantize) : `Grid (${this.gridQuantize()})`;
  }

  protected lengthText(cell: Cell): string {
    return lengthLabel(cell.lengthTicks, this.project.meter(), this.beatTicks());
  }

  protected rename(cell: Cell, name: string): void {
    const trimmed = name.trim();
    if (trimmed && trimmed !== cell.name) {
      void this.live.renameCell(cell, trimmed);
    }
  }

  protected setLength(cell: Cell, bars: number, beats: number): void {
    const ticks = Math.round(
      (Number.isFinite(bars) ? bars : 0) * this.barLength() + (Number.isFinite(beats) ? beats : 0) * this.beatTicks()
    );
    if (ticks > 0 && ticks !== cell.lengthTicks) {
      void this.live.setCellLength(cell, ticks);
    }
  }

  protected setQuantize(cell: Cell, quantize: Quantize): void {
    void this.live.setCellQuantize([cell], quantize);
  }
}
