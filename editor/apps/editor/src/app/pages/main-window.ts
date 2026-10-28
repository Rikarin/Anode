import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { XuiDockManagerImports } from '@xui/dock-manager';
import { XuiSpinnerImports } from '@xui/spinner';
import { BrowsersPane } from '../browsers/browsers-pane';
import { Inspector } from '../inspector/inspector';
import { LibraryPane } from '../library/library-pane';
import { MarkerList } from '../lists/marker-list';
import { KeyboardPane } from '../live/keyboard-pane';
import { MixerPane } from '../mixer/mixer-pane';
import { NotePad } from '../notes/note-pad';
import { PianoRoll } from '../piano-roll/piano-roll';
import { ProjectStore } from '../state/project.store';
import { ScreensetStore } from '../state/screenset.store';
import { TracksArea } from '../tracks/tracks-area';
import { ProjectChooser } from './project-chooser';

export { defaultLayout } from './layout';

/**
 * The main window's areas, as in Logic: Library and Inspector on the left, the Tracks area in the middle, the editors
 * (the Piano Roll), the Mixer and the Keyboard below, the Browsers, the List Editors (the Marker List) and the Note Pad
 * on the right. The layout is the current screenset's (ScreensetStore): a serialisable tree the screensets keep in the
 * project, recalled with 1–9. With no project open, the window shows the project chooser.
 */
@Component({
  selector: 'an-main-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BrowsersPane,
    Inspector,
    KeyboardPane,
    LibraryPane,
    MarkerList,
    MixerPane,
    NotePad,
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
      <xui-dock-manager class="h-full" [(layout)]="screensets.layout">
        <ng-template xuiDockContent="library">
          <an-library-pane />
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
        <ng-template xuiDockContent="browsers">
          <an-browsers-pane />
        </ng-template>
        <ng-template xuiDockContent="lists">
          <an-marker-list />
        </ng-template>
        <ng-template xuiDockContent="notes">
          <an-note-pad />
        </ng-template>
      </xui-dock-manager>
    }
  `
})
export class MainWindow {
  protected readonly project = inject(ProjectStore);
  protected readonly screensets = inject(ScreensetStore);
}
