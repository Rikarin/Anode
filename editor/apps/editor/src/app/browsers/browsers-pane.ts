import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { AudioFile, FileEntry } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import {
  matArrowBackRound,
  matArrowUpwardRound,
  matAudioFileRound,
  matBookmarkBorderRound,
  matBookmarkRound,
  matFolderRound,
  matInsertDriveFileRound,
  matMusicNoteRound,
  matPlayArrowRound,
  matStopRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiSpinnerImports } from '@xui/spinner';
import { XuiTabsImports } from '@xui/tabs';
import { XuiTooltipImports } from '@xui/tooltip';
import { audioFormat, BrowserStore, fileSize, ProjectFileType, ServerFileType } from '../state/browser.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import { AudioImport } from '../tracks/audio-import';

const KindIcons: Record<FileEntry['kind'], string> = {
  folder: 'matFolderRound',
  audio: 'matAudioFileRound',
  midi: 'matMusicNoteRound',
  project: 'matFolderRound',
  instrument: 'matInsertDriveFileRound',
  other: 'matInsertDriveFileRound'
};

/**
 * The Browsers pane (Logic's): Project, the audio files of the project (drag one to a track to place it); and All
 * Files, the Server's disk with bookmarks, back and up, a filter, a preview of audio files, and dragging audio or MIDI
 * files onto the Tracks area to import them (double-click imports at the playhead).
 */
@Component({
  selector: 'an-browsers-pane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiIconImports, XuiInputImports, XuiSpinnerImports, XuiTabsImports, XuiTooltipImports],
  providers: [
    provideIcons({
      matArrowBackRound,
      matArrowUpwardRound,
      matAudioFileRound,
      matBookmarkBorderRound,
      matBookmarkRound,
      matFolderRound,
      matInsertDriveFileRound,
      matMusicNoteRound,
      matPlayArrowRound,
      matStopRound
    })
  ],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Browsers' },
  template: `
    <xui-tabs class="flex min-h-0 flex-1 flex-col" [(selectedTabId)]="tab">
      <xui-tab id="files" title="All Files">
        <div class="flex min-h-0 flex-1 flex-col" data-testid="all-files">
          <div class="glass-bar flex h-9 shrink-0 items-center gap-1 px-2">
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              aria-label="Back"
              [disabled]="!browser.canGoBack()"
              (click)="browser.back()"
            >
              <ng-icon xui name="matArrowBackRound" size="sm" />
            </button>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              aria-label="Enclosing folder"
              [disabled]="!browser.listing()?.parent"
              (click)="browser.up()"
            >
              <ng-icon xui name="matArrowUpwardRound" size="sm" />
            </button>
            <span class="text-foreground min-w-0 flex-1 truncate text-[11px] font-semibold" [attr.title]="path()">
              {{ folderName() }}
            </span>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              [attr.aria-label]="browser.bookmarked() ? 'Remove bookmark' : 'Bookmark this folder'"
              [attr.aria-pressed]="browser.bookmarked()"
              data-testid="bookmark"
              (click)="browser.toggleBookmark()"
            >
              <ng-icon xui [name]="browser.bookmarked() ? 'matBookmarkRound' : 'matBookmarkBorderRound'" size="sm" />
            </button>
          </div>
          @if (browser.bookmarks().length > 0) {
            <nav class="border-hairline flex flex-wrap gap-1 border-b px-2 py-1.5" aria-label="Bookmarks">
              @for (bookmark of browser.bookmarks(); track bookmark) {
                <button
                  type="button"
                  class="bezel text-foreground h-5.5 max-w-32 truncate px-2 text-[11px] font-medium"
                  [attr.title]="bookmark"
                  [attr.aria-pressed]="bookmark === path() ? 'true' : 'false'"
                  (click)="browser.open(bookmark)"
                >
                  {{ baseName(bookmark) }}
                </button>
              }
            </nav>
          }
          <div class="px-2 py-1.5">
            <input
              #filter
              xuiInput
              type="search"
              class="h-6 text-xs"
              placeholder="Filter"
              aria-label="Filter files"
              [value]="browser.filter()"
              (input)="browser.filter.set(filter.value)"
            />
          </div>
          @if (browser.loading()) {
            <div class="grid flex-1 place-items-center"><xui-spinner size="sm" aria-label="Listing the folder" /></div>
          } @else {
            <ul class="min-h-0 flex-1 overflow-y-auto pb-2" role="list" aria-label="Files">
              @for (entry of browser.entries(); track entry.path) {
                <li
                  class="hover:bg-hover-overlay group flex h-7 items-center gap-2 px-2 text-xs"
                  [attr.draggable]="importable(entry)"
                  [attr.data-testid]="'file-' + entry.name"
                  (dragstart)="dragFile(entry, $event)"
                  (dblclick)="activate(entry)"
                >
                  <ng-icon
                    xui
                    size="sm"
                    class="shrink-0"
                    [class.text-primary]="entry.kind === 'folder'"
                    [class.text-foreground-muted]="entry.kind !== 'folder'"
                    [name]="icon(entry)"
                    aria-hidden="true"
                  />
                  <button
                    type="button"
                    class="text-foreground min-w-0 flex-1 truncate text-start focus-visible:outline-none"
                    [attr.aria-label]="entry.kind === 'folder' ? 'Open ' + entry.name : entry.name"
                    [attr.title]="describe(entry)"
                    (click)="entry.kind === 'folder' && browser.open(entry.path)"
                    (keydown.enter)="activate(entry)"
                  >
                    {{ entry.name }}
                  </button>
                  @if (entry.kind === 'audio') {
                    <span class="text-foreground-muted readout shrink-0 text-[10px]">{{ format(entry) }}</span>
                    <button
                      xuiButton
                      type="button"
                      size="sm"
                      variant="ghost"
                      class="h-5 w-5 shrink-0 px-0"
                      [attr.aria-label]="
                        (browser.previewing() === entry.path ? 'Stop preview of ' : 'Preview ') + entry.name
                      "
                      [attr.aria-pressed]="browser.previewing() === entry.path"
                      (click)="browser.preview(entry)"
                    >
                      <ng-icon
                        xui
                        size="sm"
                        [name]="browser.previewing() === entry.path ? 'matStopRound' : 'matPlayArrowRound'"
                      />
                    </button>
                  } @else if (entry.kind !== 'folder') {
                    <span class="text-foreground-muted readout shrink-0 text-[10px]">{{ size(entry.size) }}</span>
                  }
                </li>
              } @empty {
                <li class="text-foreground-muted p-3 text-xs">Nothing here{{ browser.filter() ? ' matches' : '' }}.</li>
              }
            </ul>
          }
        </div>
      </xui-tab>
      <xui-tab id="project" title="Project">
        <ul
          class="min-h-0 flex-1 overflow-y-auto py-1"
          role="list"
          aria-label="Project audio"
          data-testid="project-audio"
        >
          @for (file of projectFiles(); track file.id) {
            <li
              class="hover:bg-hover-overlay flex h-7 items-center gap-2 px-2 text-xs"
              draggable="true"
              [attr.data-testid]="'project-file-' + file.name"
              (dragstart)="dragProjectFile(file, $event)"
              (dblclick)="placeProjectFile(file)"
            >
              <ng-icon
                xui
                size="sm"
                class="text-foreground-muted shrink-0"
                name="matAudioFileRound"
                aria-hidden="true"
              />
              <span class="text-foreground min-w-0 flex-1 truncate" [attr.title]="file.path">{{ file.name }}</span>
              <span class="text-foreground-muted readout shrink-0 text-[10px]">
                {{ format({ sampleRate: file.sampleRate, channels: file.channels, frames: file.frames }) }}
              </span>
              <span class="text-foreground-muted readout w-6 shrink-0 text-end text-[10px]" title="Regions using it">{{
                uses().get(file.id) ?? 0
              }}</span>
            </li>
          } @empty {
            <li class="text-foreground-muted p-3 text-xs">
              The project has no audio files yet. Record, or import from All Files.
            </li>
          }
        </ul>
      </xui-tab>
    </xui-tabs>
  `
})
export class BrowsersPane {
  protected readonly browser = inject(BrowserStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly transport = inject(TransportStore);
  private readonly audioImport = inject(AudioImport);

  protected readonly tab = signal<string | null>('files');
  protected readonly path = computed(() => this.browser.listing()?.path ?? '');
  protected readonly folderName = computed(() => this.baseName(this.path()) || 'Home');
  protected readonly projectFiles = computed(() =>
    [...this.project.files().values()].sort((a, b) => a.name.localeCompare(b.name))
  );
  /** How many regions and takes use each file. */
  protected readonly uses = computed(() => {
    const uses = new Map<string, number>();
    for (const region of this.project.regionList()) {
      uses.set(region.fileId, (uses.get(region.fileId) ?? 0) + 1);
    }
    for (const folder of this.project.takeFolderList()) {
      for (const take of folder.takes) {
        uses.set(take.fileId, (uses.get(take.fileId) ?? 0) + 1);
      }
    }
    return uses;
  });

  constructor() {
    void this.browser.start();
  }

  protected baseName(path: string): string {
    return path.split('/').filter(Boolean).pop() ?? path;
  }

  protected icon(entry: FileEntry): string {
    return KindIcons[entry.kind];
  }

  protected format(entry: Pick<FileEntry, 'sampleRate' | 'channels' | 'frames'>): string {
    return audioFormat(entry);
  }

  protected size(bytes: number): string {
    return fileSize(bytes);
  }

  protected describe(entry: FileEntry): string {
    return entry.kind === 'audio' ? `${entry.path}\n${audioFormat(entry)}` : entry.path;
  }

  protected importable(entry: FileEntry): boolean {
    return entry.kind === 'audio' || entry.kind === 'midi';
  }

  protected dragFile(entry: FileEntry, event: DragEvent): void {
    if (!this.importable(entry) || !event.dataTransfer) {
      event.preventDefault();
      return;
    }
    event.dataTransfer.effectAllowed = 'copy';
    event.dataTransfer.setData(ServerFileType, JSON.stringify({ path: entry.path, kind: entry.kind }));
    event.dataTransfer.setData('text/plain', entry.path);
  }

  protected dragProjectFile(file: AudioFile, event: DragEvent): void {
    event.dataTransfer?.setData(ProjectFileType, file.id);
    event.dataTransfer?.setData('text/plain', file.name);
  }

  /** Double-click or Return: open a folder; import an audio file onto the selected track at the playhead, or a MIDI file. */
  protected activate(entry: FileEntry): void {
    if (entry.kind === 'folder') {
      void this.browser.open(entry.path);
    } else if (entry.kind === 'audio' || entry.kind === 'midi') {
      void this.audioImport.importServerFile(
        entry.path,
        entry.kind,
        this.selection.track(),
        this.transport.positionAt(performance.now())
      );
    }
  }

  protected placeProjectFile(file: AudioFile): void {
    void this.audioImport.placeProjectFile(
      file.id,
      this.selection.track(),
      this.transport.positionAt(performance.now())
    );
  }
}
