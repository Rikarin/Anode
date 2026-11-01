import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { FloatingWindow } from '../shell/floating-window';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { ScoreEditor } from './score-editor';

/** The Score Editor in a window (Logic's Score window): a score set's full score, or a part extracted from one. */
@Component({
  selector: 'an-score-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow, ScoreEditor],
  template: `
    @if (ui.scoreWindow(); as w) {
      <an-floating-window
        [title]="title()"
        testId="score-window"
        width="min(72rem, 92vw)"
        [origin]="{ x: 96, y: 72 }"
        (closed)="ui.scoreWindow.set(null)"
      >
        <div class="h-[75vh]">
          <an-score-editor [setId]="w.setId" [partTrackId]="w.partTrackId" />
        </div>
      </an-floating-window>
    }
  `
})
export class ScoreWindow {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);

  protected readonly title = computed(() => {
    const w = this.ui.scoreWindow();
    if (w?.setId) {
      return `Score: ${this.project.score().sets.find(s => s.id === w.setId)?.name ?? 'Score Set'}`;
    }
    return `Part: ${this.project.track(w?.partTrackId ?? '')?.name ?? ''}`;
  });
}
