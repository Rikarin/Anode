import { ChangeDetectionStrategy, Component, booleanAttribute, input, output } from '@angular/core';

/** The Score Editor's views: one long system, or pages. */
export type ScoreToolbarView = 'linear' | 'page';

/**
 * The Score Editor's toolbar, the presentational part (DESIGN.md "Phase 9 patterns"): a `glass-bar` row with the Part
 * box toggle, what the score shows (a part or a score set, as text), the view (Linear / Page, a radio group), zoom
 * (−, the percentage, +), Follow, and the score windows (Staff Styles, Score Sets, Layout) and Print. The owner holds
 * the state and acts on the outputs.
 */
@Component({
  selector: 'an-score-toolbar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'glass-bar flex h-9 shrink-0 items-center gap-2 px-2 select-none',
    role: 'toolbar',
    'aria-label': 'Score Editor',
    'data-testid': 'score-toolbar'
  },
  template: `
    <button
      type="button"
      class="bezel text-foreground h-6.5 px-2 text-[11px] font-semibold"
      [class.text-lit]="partBox()"
      [attr.aria-pressed]="partBox()"
      data-testid="score-part-box-toggle"
      (click)="partBoxChange.emit(!partBox())"
    >
      Part Box
    </button>
    <span class="text-foreground min-w-0 truncate text-[12px] font-semibold" data-testid="score-title">{{
      title()
    }}</span>
    <div class="flex-1"></div>
    <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold" role="radiogroup" aria-label="Score view">
      @for (option of views; track option.value) {
        <button
          type="button"
          role="radio"
          class="px-2"
          [class.text-lit]="view() === option.value"
          [attr.aria-checked]="view() === option.value"
          [attr.tabindex]="view() === option.value ? 0 : -1"
          [attr.data-testid]="'score-view-' + option.value"
          (click)="viewChange.emit(option.value)"
          (keydown.arrowLeft)="viewChange.emit('linear')"
          (keydown.arrowRight)="viewChange.emit('page')"
        >
          {{ option.label }}
        </button>
      }
    </div>
    <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold" role="group" aria-label="Zoom">
      <button
        type="button"
        class="w-6.5 disabled:opacity-35"
        aria-label="Zoom out"
        [disabled]="zoom() <= minZoom()"
        (click)="zoomBy.emit(-1)"
      >
        −
      </button>
      <span class="readout grid w-12 place-items-center" data-testid="score-zoom" aria-live="polite"
        >{{ zoom() }} %</span
      >
      <button
        type="button"
        class="w-6.5 disabled:opacity-35"
        aria-label="Zoom in"
        [disabled]="zoom() >= maxZoom()"
        (click)="zoomBy.emit(1)"
      >
        +
      </button>
    </div>
    <button
      type="button"
      class="bezel text-foreground h-6.5 px-2 text-[11px] font-semibold"
      [class.text-lit]="follow()"
      [attr.aria-pressed]="follow()"
      data-testid="score-follow"
      (click)="followChange.emit(!follow())"
    >
      Follow
    </button>
    <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold" role="group" aria-label="Score windows">
      <button type="button" class="px-2" data-testid="score-open-styles" (click)="staffStyles.emit()">
        Staff Styles…
      </button>
      <button type="button" class="px-2" data-testid="score-open-sets" (click)="scoreSets.emit()">Score Sets…</button>
      <button type="button" class="px-2" data-testid="score-open-layout" (click)="layout.emit()">Layout…</button>
    </div>
    <button
      type="button"
      class="bezel text-foreground h-6.5 px-2 text-[11px] font-semibold disabled:opacity-35"
      data-testid="score-print"
      [disabled]="printDisabled()"
      (click)="print.emit()"
    >
      Print…
    </button>
  `
})
export class AnScoreToolbar {
  readonly title = input('');
  readonly view = input<ScoreToolbarView>('linear');
  readonly zoom = input(100);
  readonly minZoom = input(50);
  readonly maxZoom = input(300);
  readonly follow = input(true, { transform: booleanAttribute });
  readonly partBox = input(true, { transform: booleanAttribute });
  readonly printDisabled = input(false, { transform: booleanAttribute });
  readonly viewChange = output<ScoreToolbarView>();
  readonly zoomBy = output<1 | -1>();
  readonly followChange = output<boolean>();
  readonly partBoxChange = output<boolean>();
  readonly staffStyles = output();
  readonly scoreSets = output();
  readonly layout = output();
  readonly print = output();

  protected readonly views: readonly { value: ScoreToolbarView; label: string }[] = [
    { value: 'linear', label: 'Linear' },
    { value: 'page', label: 'Page' }
  ];
}
