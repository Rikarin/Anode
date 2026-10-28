import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { Take, TakeFolder } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matMoreHorizRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { ProjectStore } from '../state/project.store';
import { wholeTake } from './comp';

/**
 * A take's row header under an open take folder (Logic's): the take's name, whether the comp plays it all, some or none
 * of it, and its menu: select it as the whole comp, or delete it.
 */
@Component({
  selector: 'an-take-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiIconImports, XuiMenuImports],
  providers: [provideIcons({ matMoreHorizRound })],
  host: {
    class: 'an-track-header border-hairline bg-surface-raised flex items-center gap-2 border-b ps-6 pe-2',
    role: 'group',
    '[attr.aria-label]': '"Take: " + take().name'
  },
  template: `
    <span
      class="size-1.5 shrink-0 rounded-full"
      [class.bg-primary]="state() !== 'none'"
      [class.bg-border-strong]="state() === 'none'"
      role="img"
      [attr.aria-label]="
        state() === 'all' ? 'Whole take in the comp' : state() === 'some' ? 'Partly in the comp' : 'Not in the comp'
      "
    ></span>
    <span class="text-foreground min-w-0 flex-1 truncate text-[12px] font-medium">{{ take().name }}</span>
    <button
      xuiButton
      type="button"
      size="sm"
      variant="ghost"
      class="h-5 w-auto px-1.5 text-[11px]"
      [attr.aria-label]="'Select ' + take().name"
      [disabled]="state() === 'all'"
      (click)="select()"
    >
      Select
    </button>
    <button
      xuiButton
      type="button"
      variant="ghost"
      size="sm"
      class="h-5 w-5 px-0"
      [attr.aria-label]="'More for ' + take().name"
      [xuiMenuTriggerFor]="menu"
    >
      <ng-icon xui name="matMoreHorizRound" size="sm" />
    </button>
    <ng-template #menu>
      <xui-menu>
        <button xuiMenuItem type="button" (click)="select()">Select Take</button>
        <button xuiMenuItem type="button" color="error" [disabled]="folder().takes.length < 2" (click)="remove()">
          Delete Take
        </button>
      </xui-menu>
    </ng-template>
  `
})
export class TakeHeader {
  private readonly project = inject(ProjectStore);
  readonly folder = input.required<TakeFolder>();
  readonly take = input.required<Take>();

  protected readonly state = computed<'all' | 'some' | 'none'>(() => {
    const folder = this.folder();
    if (wholeTake(folder) === this.take().id) {
      return 'all';
    }
    return folder.comp.some(s => s.takeId === this.take().id) ? 'some' : 'none';
  });

  protected select(): void {
    void this.project.execute(
      [{ type: 'selectTake', folderId: this.folder().id, takeId: this.take().id }],
      'Select Take'
    );
  }

  protected remove(): void {
    void this.project.execute(
      [{ type: 'deleteTake', folderId: this.folder().id, takeId: this.take().id }],
      'Delete Take'
    );
  }
}
