import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { XuiDockManagerImports, type XuiDockManagerLayout } from '@xui/dock-manager';
import { XuiSpinnerImports } from '@xui/spinner';
import { Inspector } from '../inspector/inspector';
import { KeyboardPane } from '../live/keyboard-pane';
import { MixerPane } from '../mixer/mixer-pane';
import { PianoRoll } from '../piano-roll/piano-roll';
import { ProjectStore } from '../state/project.store';
import { TracksArea } from '../tracks/tracks-area';
import { ProjectChooser } from './project-chooser';

/**
 * The main window's areas, as in Logic: Library and Inspector on the left, the Tracks area in the middle, the editors
 * (the Piano Roll), the Mixer and the Keyboard below, the list editors on the right. The Library and the list editors
 * are placeholders until their phases land (docs/01-implementation-plan.md §6.1, §12). The layout is a serialisable
 * tree, which is what screensets will store. With no project open, the window shows the project chooser.
 */
@Component({
  selector: 'an-main-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    Inspector,
    KeyboardPane,
    MixerPane,
    PianoRoll,
    ProjectChooser,
    TracksArea,
    XuiDockManagerImports,
    XuiSpinnerImports
  ],
  host: { class: 'block h-full' },
  template: `
    @if (!project.isOpen()) {
      @if (project.loading()) {
        <div class="grid h-full place-items-center"><xui-spinner aria-label="Opening the project" /></div>
      } @else {
        <an-project-chooser />
      }
    } @else {
      <xui-dock-manager class="h-full" [(layout)]="layout">
        <ng-template xuiDockContent="library">
          <section class="text-foreground-muted p-3 text-sm" aria-label="Library">Patches and presets.</section>
        </ng-template>
        <ng-template xuiDockContent="inspector">
          <an-inspector />
        </ng-template>
        <ng-template xuiDockContent="tracks">
          <an-tracks-area />
        </ng-template>
        <ng-template xuiDockContent="editors">
          <an-piano-roll />
        </ng-template>
        <ng-template xuiDockContent="keyboard">
          <an-keyboard-pane />
        </ng-template>
        <ng-template xuiDockContent="mixer">
          <an-mixer-pane />
        </ng-template>
        <ng-template xuiDockContent="lists">
          <section class="text-foreground-muted p-3 text-sm" aria-label="List editors">
            Event, Marker, Tempo and Signature lists.
          </section>
        </ng-template>
      </xui-dock-manager>
    }
  `
})
export class MainWindow {
  protected readonly project = inject(ProjectStore);
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
            { type: 'contentPane', contentId: 'library', header: 'Library', size: 1, allowClose: false },
            { type: 'contentPane', contentId: 'inspector', header: 'Inspector', size: 3, allowClose: false }
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
                { type: 'contentPane', contentId: 'mixer', header: 'Mixer', allowClose: false },
                { type: 'contentPane', contentId: 'editors', header: 'Editors', allowClose: false },
                { type: 'contentPane', contentId: 'keyboard', header: 'Keyboard', allowClose: false }
              ]
            }
          ]
        },
        { type: 'contentPane', contentId: 'lists', header: 'List Editors', size: 1, isPinned: false }
      ]
    }
  };
}
