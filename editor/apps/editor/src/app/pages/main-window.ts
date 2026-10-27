import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { XuiDockManagerImports, type XuiDockManagerLayout } from '@xui/dock-manager';

/**
 * The main window's areas, as in Logic: Library and inspector on the left, the Tracks area in the middle, the editors
 * and the Mixer below, the list editors on the right. Each is a placeholder until its phase lands
 * (docs/01-implementation-plan.md §6.1, §12). The layout is a serialisable tree, which is what screensets will store.
 */
@Component({
  selector: 'an-main-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiDockManagerImports],
  host: { class: 'block h-full' },
  template: `
    <xui-dock-manager class="h-full" [(layout)]="layout">
      <ng-template xuiDockContent="library">
        <section class="text-foreground-muted p-3 text-sm" aria-label="Library">Patches and presets.</section>
      </ng-template>
      <ng-template xuiDockContent="inspector">
        <section class="text-foreground-muted p-3 text-sm" aria-label="Inspector">Region and track parameters.</section>
      </ng-template>
      <ng-template xuiDockContent="tracks">
        <section class="text-foreground-muted grid h-full place-items-center text-sm" aria-label="Tracks">
          No tracks yet.
        </section>
      </ng-template>
      <ng-template xuiDockContent="editors">
        <section class="text-foreground-muted p-3 text-sm" aria-label="Editors">
          Audio Track Editor, Piano Roll, Score, Step Sequencer.
        </section>
      </ng-template>
      <ng-template xuiDockContent="mixer">
        <section class="text-foreground-muted p-3 text-sm" aria-label="Mixer">Channel strips.</section>
      </ng-template>
      <ng-template xuiDockContent="lists">
        <section class="text-foreground-muted p-3 text-sm" aria-label="List editors">
          Event, Marker, Tempo and Signature lists.
        </section>
      </ng-template>
    </xui-dock-manager>
  `
})
export class MainWindow {
  protected readonly layout = signal<XuiDockManagerLayout>(defaultLayout());
}

/** The layout a new project opens with. */
export function defaultLayout(): XuiDockManagerLayout {
  return {
    rootPane: {
      type: 'splitPane',
      orientation: 'horizontal',
      panes: [
        {
          type: 'splitPane',
          orientation: 'vertical',
          size: 1,
          panes: [
            { type: 'contentPane', contentId: 'library', header: 'Library', allowClose: false },
            { type: 'contentPane', contentId: 'inspector', header: 'Inspector', allowClose: false }
          ]
        },
        {
          type: 'splitPane',
          orientation: 'vertical',
          size: 4,
          panes: [
            { type: 'contentPane', contentId: 'tracks', header: 'Tracks', size: 3, allowClose: false },
            {
              type: 'tabGroupPane',
              size: 2,
              panes: [
                { type: 'contentPane', contentId: 'editors', header: 'Editors', allowClose: false },
                { type: 'contentPane', contentId: 'mixer', header: 'Mixer', allowClose: false }
              ]
            }
          ]
        },
        { type: 'contentPane', contentId: 'lists', header: 'List Editors', size: 1, isPinned: false }
      ]
    }
  };
}
