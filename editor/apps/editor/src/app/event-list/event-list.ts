import { ChangeDetectionStrategy, Component } from '@angular/core';

/** The Event List, an editor of the Editors pane (docs/08-phase-7.md). Placeholder until its workstream lands. */
@Component({
  selector: 'an-event-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex h-full min-h-0 flex-col' },
  template: `<p class="text-foreground-muted p-4 text-[12px]">Event List</p>`
})
export class EventList {
  /** Placeholder: replaced by the editor. */
  protected readonly placeholder = true;
}
