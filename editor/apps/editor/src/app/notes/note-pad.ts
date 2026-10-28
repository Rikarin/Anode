import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { XuiTabsImports } from '@xui/tabs';
import { XuiTextareaImports } from '@xui/textarea';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';

/**
 * The Note Pad (Logic's): the project's notes and the selected track's, each saved when the field loses focus as one
 * undo step (`setProjectNotes`, `setTrack` with `notes`).
 */
@Component({
  selector: 'an-note-pad',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiTabsImports, XuiTextareaImports],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Note Pad' },
  template: `
    <xui-tabs class="flex min-h-0 flex-1 flex-col" [(selectedTabId)]="tab">
      <xui-tab id="project" title="Project">
        <div class="flex h-full min-h-0 flex-col p-2">
          <textarea
            #projectNotes
            xuiTextarea
            class="min-h-32 flex-1 resize-none text-xs"
            aria-label="Project notes"
            data-testid="project-notes"
            placeholder="Notes about the project: lyrics, ideas, what to do next."
            [value]="project.notes()"
            (blur)="saveProject(projectNotes.value)"
          ></textarea>
        </div>
      </xui-tab>
      <xui-tab id="track" title="Track">
        <div class="flex h-full min-h-0 flex-col p-2">
          @if (track(); as t) {
            <textarea
              #trackNotes
              xuiTextarea
              class="min-h-32 flex-1 resize-none text-xs"
              [attr.aria-label]="'Notes of ' + t.name"
              data-testid="track-notes"
              placeholder="Notes about this track: mic, settings, takes."
              [value]="t.notes"
              (blur)="saveTrack(t.id, trackNotes.value)"
            ></textarea>
          } @else {
            <p class="text-foreground-muted p-1 text-xs">Select a track to see its notes.</p>
          }
        </div>
      </xui-tab>
    </xui-tabs>
  `
})
export class NotePad {
  protected readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  protected readonly tab = signal<string | null>('project');
  protected readonly track = computed(() => {
    const id = this.selection.track();
    return id ? (this.project.tracks().get(id) ?? null) : null;
  });

  protected saveProject(text: string): void {
    if (text !== this.project.notes()) {
      void this.project.execute([{ type: 'setProjectNotes', text }], 'Project Notes');
    }
  }

  protected saveTrack(trackId: string, notes: string): void {
    if (notes !== this.project.track(trackId)?.notes) {
      void this.project.execute([{ type: 'setTrack', trackId, input: null, notes }], 'Track Notes');
    }
  }
}
