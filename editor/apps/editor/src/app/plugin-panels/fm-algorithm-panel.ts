import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input } from '@angular/core';
import { PanelParameter } from './panel-parameter';
import { panelBinding } from './panel-patterns';
import type { PluginPanelHost } from './plugin-panel';

/** An FM algorithm: where each operator (1–4) sits in the diagram (column, row; carriers on the bottom row) and which modulates which. */
export interface FmAlgorithm {
  readonly number: number;
  readonly operators: readonly (readonly [number, number])[];
  /** [modulator, carrier] operator numbers. */
  readonly links: readonly (readonly [number, number])[];
  readonly carriers: readonly number[];
}

/** The eight four-operator algorithms (as the classic four-operator FM synths number them). */
export const FmAlgorithms: readonly FmAlgorithm[] = [
  {
    number: 1,
    operators: [
      [1.5, 3],
      [1.5, 2],
      [1.5, 1],
      [1.5, 0]
    ],
    links: [
      [4, 3],
      [3, 2],
      [2, 1]
    ],
    carriers: [1]
  },
  {
    number: 2,
    operators: [
      [1.5, 3],
      [1.5, 2],
      [1, 1],
      [2, 1]
    ],
    links: [
      [3, 2],
      [4, 2],
      [2, 1]
    ],
    carriers: [1]
  },
  {
    number: 3,
    operators: [
      [1.5, 3],
      [1, 2],
      [1, 1],
      [2, 2]
    ],
    links: [
      [3, 2],
      [2, 1],
      [4, 1]
    ],
    carriers: [1]
  },
  {
    number: 4,
    operators: [
      [1.5, 3],
      [1, 2],
      [2, 2],
      [2, 1]
    ],
    links: [
      [4, 3],
      [3, 1],
      [2, 1]
    ],
    carriers: [1]
  },
  {
    number: 5,
    operators: [
      [1, 3],
      [1, 2],
      [2, 3],
      [2, 2]
    ],
    links: [
      [2, 1],
      [4, 3]
    ],
    carriers: [1, 3]
  },
  {
    number: 6,
    operators: [
      [0.5, 3],
      [1.5, 3],
      [2.5, 3],
      [1.5, 2]
    ],
    links: [
      [4, 1],
      [4, 2],
      [4, 3]
    ],
    carriers: [1, 2, 3]
  },
  {
    number: 7,
    operators: [
      [0.5, 3],
      [1.5, 3],
      [2.5, 3],
      [2.5, 2]
    ],
    links: [[4, 3]],
    carriers: [1, 2, 3]
  },
  {
    number: 8,
    operators: [
      [0, 3],
      [1, 3],
      [2, 3],
      [3, 3]
    ],
    links: [],
    carriers: [1, 2, 3, 4]
  }
];

const BoxW = 9;
const BoxH = 7;
const X = (col: number) => 3 + col * 12;
const Y = (row: number) => 3 + row * 11;

/**
 * The FM synth's panel: the eight algorithms as a radio group of small operator diagrams (boxes for operators,
 * lines from modulators down to the carriers they modulate, carriers on a bar to the output), the selected one lit;
 * then each operator's ratio and level as a row of knobs, and the feedback.
 */
@Component({
  selector: 'an-fm-algorithm-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelParameter],
  host: { class: 'flex flex-col gap-3', role: 'group', 'aria-label': 'FM algorithm', 'data-testid': 'fm-panel' },
  template: `
    <div class="flex flex-wrap gap-1" role="radiogroup" aria-label="Algorithm">
      @for (algorithm of algorithms; track algorithm.number) {
        <button
          type="button"
          role="radio"
          class="bezel text-foreground-muted flex flex-col items-center gap-0.5 px-1 pt-1 pb-0.5"
          [class.text-lit]="algorithm.number === selected()"
          [attr.aria-checked]="algorithm.number === selected()"
          [attr.aria-label]="'Algorithm ' + algorithm.number"
          [attr.tabindex]="algorithm.number === selected() ? 0 : -1"
          (click)="choose(algorithm.number)"
          (keydown)="key($event)"
        >
          <svg viewBox="0 0 48 47" class="h-9 w-9" aria-hidden="true">
            @for (link of algorithm.links; track $index) {
              <line
                [attr.x1]="cx(algorithm, link[0])"
                [attr.y1]="bottom(algorithm, link[0])"
                [attr.x2]="cx(algorithm, link[1])"
                [attr.y2]="top(algorithm, link[1])"
                stroke="currentColor"
                stroke-width="1"
              />
            }
            <line
              [attr.x1]="outX(algorithm)[0]"
              [attr.x2]="outX(algorithm)[1]"
              y1="45"
              y2="45"
              stroke="currentColor"
              stroke-width="1.5"
            />
            @for (op of algorithm.operators; track $index; let i = $index) {
              @if (algorithm.carriers.includes(i + 1)) {
                <line
                  [attr.x1]="cx(algorithm, i + 1)"
                  [attr.x2]="cx(algorithm, i + 1)"
                  [attr.y1]="bottom(algorithm, i + 1)"
                  y2="45"
                  stroke="currentColor"
                  stroke-width="1"
                />
              }
              <rect
                [attr.x]="X(op[0])"
                [attr.y]="Y(op[1])"
                [attr.width]="boxW"
                [attr.height]="boxH"
                rx="1.5"
                [attr.fill]="algorithm.carriers.includes(i + 1) ? 'currentColor' : 'none'"
                [attr.fill-opacity]="0.35"
                stroke="currentColor"
                stroke-width="1"
              />
            }
          </svg>
          <span class="readout text-foreground text-[10px] leading-none">{{ algorithm.number }}</span>
        </button>
      }
    </div>
    <div class="flex flex-col gap-1" role="list" aria-label="Operators">
      @for (op of operators(); track op.number) {
        <div class="flex items-end gap-1" role="listitem" [attr.aria-label]="'Operator ' + op.number">
          <span class="text-foreground-muted w-10 self-center text-[10px] font-semibold tracking-wide uppercase"
            >Op {{ op.number }}</span
          >
          <an-panel-parameter [host]="host()" [parameter]="op.ratio" label="Ratio" />
          <an-panel-parameter [host]="host()" [parameter]="op.level" label="Level" />
        </div>
      }
    </div>
    @if (feedback(); as f) {
      <div class="flex items-end gap-1">
        <an-panel-parameter [host]="host()" [parameter]="f" />
      </div>
    }
  `
})
export class FmAlgorithmPanel {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly host = input.required<PluginPanelHost>();

  protected readonly algorithms = FmAlgorithms;
  protected readonly boxW = BoxW;
  protected readonly boxH = BoxH;
  protected readonly X = X;
  protected readonly Y = Y;

  private readonly binding = computed(() => panelBinding(this.host().plugin, 'fmAlgorithm'));
  private readonly algorithm = computed(() => this.binding()?.roles['algorithm']);
  protected readonly feedback = computed(() => this.binding()?.roles['feedback']);
  protected readonly operators = computed(() => {
    const ratios = this.binding()?.series['ratio'] ?? [];
    const levels = this.binding()?.series['level'] ?? [];
    return ratios.slice(0, levels.length).map((ratio, i) => ({ number: i + 1, ratio, level: levels[i] ?? ratio }));
  });
  /** The algorithm chosen now, 1–8. */
  protected readonly selected = computed(() => {
    const parameter = this.algorithm();
    return parameter ? Math.round(this.host().value(parameter.id) - parameter.min) + 1 : 1;
  });

  protected cx(algorithm: FmAlgorithm, op: number): number {
    const [col = 0] = algorithm.operators[op - 1] ?? [];
    return X(col) + BoxW / 2;
  }

  protected top(algorithm: FmAlgorithm, op: number): number {
    const [, row = 0] = algorithm.operators[op - 1] ?? [];
    return Y(row);
  }

  protected bottom(algorithm: FmAlgorithm, op: number): number {
    return this.top(algorithm, op) + BoxH;
  }

  /** The output bar under the carriers. */
  protected outX(algorithm: FmAlgorithm): [number, number] {
    const xs = algorithm.carriers.map(c => this.cx(algorithm, c));
    return [Math.min(...xs) - 3, Math.max(...xs) + 3];
  }

  protected choose(number: number): void {
    const parameter = this.algorithm();
    if (parameter && number !== this.selected()) {
      this.host().set(parameter.id, parameter.min + number - 1);
    }
  }

  /** Arrows move the choice (and focus) through the algorithms, as a radio group does. */
  protected key(event: KeyboardEvent): void {
    const step = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[event.key];
    const target =
      step !== undefined
        ? ((this.selected() - 1 + step + 8) % 8) + 1
        : event.key === 'Home'
          ? 1
          : event.key === 'End'
            ? 8
            : 0;
    if (target === 0) {
      return;
    }
    event.preventDefault();
    this.choose(target);
    this.element.nativeElement.querySelectorAll<HTMLElement>('[role="radio"]')[target - 1]?.focus();
  }
}
