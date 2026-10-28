import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { Track } from '@anode/api-client';
import { XuiSelectImports } from '@xui/select';
import { XuiTextareaImports } from '@xui/textarea';
import { ProjectStore } from '../state/project.store';

/** A stack choice in the picker: a stack, or none. */
interface StackChoice {
  id: string | null;
  name: string;
}

/**
 * What every track's inspector shows (Logic's track inspector, its first rows): the stack it is in (move it into
 * another stack, or out), or for a stack its members; and the track's notes (the Note Pad's track page).
 */
@Component({
  selector: 'an-track-details',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiSelectImports, XuiTextareaImports],
  host: { class: 'flex flex-col gap-2', role: 'group', '[attr.aria-label]': '"Track details: " + track().name' },
  template: `
    <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 text-xs">
      @if (track().kind === 'stack') {
        <span class="text-foreground-muted">Members</span>
        <span class="text-foreground truncate" data-testid="stack-members">{{ members() || 'None' }}</span>
      } @else {
        <span class="text-foreground-muted">Stack</span>
        <xui-select
          class="an-compact-select"
          aria-label="Track stack"
          data-testid="stack-membership"
          [items]="stacks()"
          [itemText]="stackText"
          [filterable]="false"
          [value]="current()"
          (valueChange)="move($event)"
        />
      }
    </div>
    <label class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase" [for]="notesId()"
      >Notes</label
    >
    <textarea
      #notes
      xuiTextarea
      class="min-h-16 resize-y text-xs"
      [id]="notesId()"
      data-testid="inspector-track-notes"
      placeholder="Notes about this track"
      [value]="track().notes"
      (blur)="saveNotes(notes.value)"
    ></textarea>
  `
})
export class TrackDetails {
  private readonly project = inject(ProjectStore);
  readonly track = input.required<Track>();

  protected readonly notesId = computed(() => `an-track-notes-${this.track().id}`);
  protected readonly stackText = (choice: StackChoice) => choice.name;
  protected readonly stacks = computed<StackChoice[]>(() => [
    { id: null, name: 'None' },
    ...this.project
      .trackList()
      .filter(t => t.kind === 'stack')
      .map(t => ({ id: t.id, name: t.name }))
  ]);
  protected readonly current = computed(
    () => this.stacks().find(s => s.id === (this.track().parentId ?? null)) ?? this.stacks()[0] ?? null
  );
  protected readonly members = computed(() =>
    this.project
      .trackList()
      .filter(t => t.parentId === this.track().id)
      .map(t => t.name)
      .join(', ')
  );

  protected move(choice: StackChoice | null): void {
    const parentId = choice?.id ?? null;
    if (parentId !== (this.track().parentId ?? null)) {
      void this.project.execute(
        [{ type: 'setTrackParent', trackId: this.track().id, parentId }],
        parentId ? 'Move to Stack' : 'Remove from Stack'
      );
    }
  }

  protected saveNotes(notes: string): void {
    if (notes !== this.track().notes) {
      void this.project.execute([{ type: 'setTrack', trackId: this.track().id, input: null, notes }], 'Track Notes');
    }
  }
}
