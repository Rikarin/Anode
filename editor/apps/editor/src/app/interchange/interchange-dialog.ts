import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import type { Job } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matFolderOpenRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiSegmentedControlImports } from '@xui/segmented-control';
import { Field, selectValue } from '../control/styles';
import { ANODE_API } from '../core/api';
import { ConnectionService } from '../core/connection';
import { isActive, JobsStore } from '../state/jobs.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import {
  aafRequest,
  exportFileName,
  type ExportForm,
  exportProblems,
  FormatNames,
  ImportAccept,
  type ImportForm,
  type InterchangeFormat,
  midiImportRequest,
  midiRequest,
  musicXmlRequest,
  type ScoreSource
} from './interchange';

/**
 * File ▸ Export ▸ Score as MusicXML…, Project as AAF…, Project as MIDI File… and File ▸ Import ▸ MusicXML…, AAF…, MIDI
 * File… (docs/10-phase-9.md): one dialog in the Bounce window's manner. An export names its file and folder (the
 * project's Exports folder by default) and its options — the score set or part and `.mxl` for MusicXML, the audio
 * tracks and embedded or linked media for AAF, type 0 or 1 and the tracks (or the selected region) for MIDI. An import
 * picks a file (a native dialog in the desktop app, an upload in a browser), with the MIDI options. Both run as Server
 * jobs: the dialog shows the progress and closes when it is done; a toast says where the file went.
 */
@Component({
  selector: 'an-interchange-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiIconImports, XuiInputImports, XuiSegmentedControlImports],
  providers: [provideIcons({ matFolderOpenRound })],
  template: `
    <xui-dialog
      [open]="open()"
      (openChange)="$event || close()"
      [title]="dialogTitle()"
      size="lg"
      [canOutsideClickClose]="false"
    >
      <xui-dialog-body class="flex flex-col gap-4 text-xs" data-testid="interchange-dialog">
        @if (direction() === 'export') {
          <div class="grid grid-cols-[7rem_1fr] items-center gap-x-3 gap-y-2.5">
            <label class="text-foreground-muted" for="an-export-name">Save As</label>
            <input
              #name
              id="an-export-name"
              xuiInput
              class="h-7 text-xs"
              data-testid="export-name"
              [value]="form().fileName"
              (input)="patch({ fileName: name.value })"
            />
            <label class="text-foreground-muted" for="an-export-folder">Where</label>
            <div class="flex gap-1">
              <input
                #folder
                id="an-export-folder"
                xuiInput
                class="h-7 min-w-0 flex-1 text-xs"
                placeholder="The project's Exports folder"
                data-testid="export-folder"
                [value]="form().directory"
                (input)="patch({ directory: folder.value })"
              />
              @if (chooseFolder) {
                <button xuiButton type="button" size="sm" variant="outline" (click)="pickFolder()">
                  <ng-icon xui name="matFolderOpenRound" size="sm" />
                  Choose…
                </button>
              }
            </div>
            @switch (format()) {
              @case ('musicxml') {
                <label class="text-foreground-muted" for="an-export-source">Score</label>
                <select
                  id="an-export-source"
                  [class]="field"
                  data-testid="export-source"
                  (change)="setSource(value($event))"
                >
                  <option value="all" [selected]="sourceKey() === 'all'">All MIDI tracks</option>
                  @for (set of sets(); track set.id) {
                    <option [value]="'set:' + set.id" [selected]="sourceKey() === 'set:' + set.id">
                      Score set: {{ set.name }}
                    </option>
                  }
                  @for (track of midiTracks(); track track.id) {
                    <option [value]="'part:' + track.id" [selected]="sourceKey() === 'part:' + track.id">
                      Part: {{ track.name }}
                    </option>
                  }
                </select>
                <span></span>
                <label class="text-foreground flex items-center gap-2">
                  <input
                    type="checkbox"
                    class="accent-(--color-primary)"
                    data-testid="export-compressed"
                    [checked]="form().compressed"
                    (change)="patch({ compressed: !form().compressed })"
                  />
                  Compressed (.mxl)
                </label>
              }
              @case ('aaf') {
                <span class="text-foreground-muted">Media</span>
                <xui-segmented-control
                  size="sm"
                  aria-label="AAF media"
                  data-testid="export-media"
                  [options]="mediaOptions"
                  [value]="form().embedMedia ? 'embed' : 'link'"
                  (valueChange)="patch({ embedMedia: $event !== 'link' })"
                />
                <span class="text-foreground-muted self-start pt-1">Tracks</span>
                <div [class]="trackBox" role="group" aria-label="Audio tracks">
                  @for (track of audioTracks(); track track.id) {
                    <label class="text-foreground flex min-w-0 items-center gap-2">
                      <input
                        type="checkbox"
                        class="accent-(--color-primary)"
                        [attr.data-testid]="'export-track-' + track.id"
                        [checked]="form().trackIds.includes(track.id)"
                        (change)="toggleTrack(track.id)"
                      />
                      <span class="truncate">{{ track.name }}</span>
                    </label>
                  } @empty {
                    <span class="text-foreground-muted">The project has no audio tracks.</span>
                  }
                </div>
              }
              @case ('midi') {
                <span class="text-foreground-muted">Format</span>
                <xui-segmented-control
                  size="sm"
                  aria-label="MIDI file format"
                  data-testid="export-midi-format"
                  [options]="midiFormats"
                  [value]="form().format === 0 ? 'type0' : 'type1'"
                  (valueChange)="patch({ format: $event === 'type0' ? 0 : 1 })"
                />
                @if (selectedRegion(); as region) {
                  <span></span>
                  <label class="text-foreground flex items-center gap-2">
                    <input
                      type="checkbox"
                      class="accent-(--color-primary)"
                      data-testid="export-region-only"
                      [checked]="form().regionId !== null"
                      (change)="patch({ regionId: form().regionId ? null : region.id })"
                    />
                    Only the selected region ({{ region.name }})
                  </label>
                }
                @if (form().regionId === null) {
                  <span class="text-foreground-muted self-start pt-1">Tracks</span>
                  <div [class]="trackBox" role="group" aria-label="MIDI tracks">
                    @for (track of midiTracks(); track track.id) {
                      <label class="text-foreground flex min-w-0 items-center gap-2">
                        <input
                          type="checkbox"
                          class="accent-(--color-primary)"
                          [attr.data-testid]="'export-track-' + track.id"
                          [checked]="form().trackIds.includes(track.id)"
                          (change)="toggleTrack(track.id)"
                        />
                        <span class="truncate">{{ track.name }}</span>
                      </label>
                    }
                  </div>
                }
              }
            }
          </div>
          <p class="text-foreground-muted" data-testid="export-file">Writes {{ fileName() }}</p>
          @if (touched() && problems().length) {
            <p class="text-error" role="alert">{{ problems().join(' ') }}</p>
          }
        } @else {
          <div class="grid grid-cols-[7rem_1fr] items-center gap-x-3 gap-y-2.5">
            <span class="text-foreground-muted">File</span>
            <div class="flex min-w-0 items-center gap-2">
              @if (pickFile()) {
                <button
                  xuiButton
                  type="button"
                  size="sm"
                  variant="outline"
                  data-testid="import-choose"
                  (click)="pick()"
                >
                  Choose…
                </button>
              } @else {
                <input
                  type="file"
                  class="text-foreground min-w-0 text-xs"
                  data-testid="import-file"
                  [attr.accept]="accept()"
                  [attr.aria-label]="'File to import'"
                  (change)="upload($event)"
                />
              }
              <span class="text-foreground readout min-w-0 truncate" data-testid="import-path">{{ importName() }}</span>
            </div>
            @if (format() === 'midi') {
              @for (option of midiOptions; track option.key) {
                <span></span>
                <label class="text-foreground flex items-center gap-2">
                  <input
                    type="checkbox"
                    class="accent-(--color-primary)"
                    [attr.data-testid]="'import-' + option.key"
                    [checked]="importForm()[option.key]"
                    (change)="toggleImport(option.key)"
                  />
                  {{ option.label }}
                </label>
              }
            }
          </div>
          <p class="text-foreground-muted">
            {{
              format() === 'aaf'
                ? 'The tracks, regions and media come in as new audio tracks; the media are copied into the project.'
                : 'The parts come in as new tracks with their notes' +
                  (format() === 'musicxml' ? ', symbols, lyrics and chord symbols.' : '.')
            }}
          </p>
        }
        @if (job(); as j) {
          <div class="flex items-center gap-3" data-testid="interchange-progress">
            <div
              class="well h-2 flex-1 overflow-hidden rounded-full"
              role="progressbar"
              [attr.aria-label]="dialogTitle() + ' progress'"
              aria-valuemin="0"
              aria-valuemax="100"
              [attr.aria-valuenow]="percent()"
            >
              <div class="bg-primary h-full transition-[width] duration-200" [style.width.%]="percent()"></div>
            </div>
            <span class="readout text-foreground w-24 text-end">{{ stateText(j) }}</span>
          </div>
        }
      </xui-dialog-body>
      <xui-dialog-footer>
        @if (running()) {
          <button xuiButton type="button" variant="ghost" data-testid="interchange-cancel" (click)="cancelJob()">
            Stop
          </button>
        } @else {
          <button xuiButton type="button" variant="ghost" (click)="close()">Cancel</button>
        }
        <button
          xuiButton
          type="button"
          data-testid="interchange-start"
          [disabled]="running() || !ready()"
          (click)="start()"
        >
          {{ direction() === 'export' ? 'Export' : 'Import' }}
        </button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class InterchangeDialog {
  protected readonly ui = inject(UiStore);
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly jobs = inject(JobsStore);
  private readonly desktop = inject(ConnectionService).desktop;

  protected readonly field = Field;
  protected readonly value = selectValue;
  protected readonly trackBox =
    'border-hairline bg-surface-inset grid max-h-32 grid-cols-2 gap-x-3 gap-y-1 overflow-y-auto rounded-md border p-2';
  protected readonly chooseFolder = typeof this.desktop?.dialogs?.chooseFolder === 'function';
  protected readonly mediaOptions = [
    { value: 'embed', label: 'Embed' },
    { value: 'link', label: 'Link (WAVs beside)' }
  ];
  protected readonly midiFormats = [
    { value: 'type1', label: 'Type 1 (tracks)' },
    { value: 'type0', label: 'Type 0 (one track)' }
  ];
  protected readonly midiOptions: readonly { key: 'importTempo' | 'importMarkers' | 'merge'; label: string }[] = [
    { key: 'importTempo', label: 'Import the tempo map and signatures' },
    { key: 'importMarkers', label: 'Import markers' },
    { key: 'merge', label: 'Merge every file track into one track' }
  ];

  protected readonly open = computed(() => this.ui.interchange() !== null);
  protected readonly direction = computed(() => this.ui.interchange()?.direction ?? 'export');
  protected readonly format = computed<InterchangeFormat>(() => this.ui.interchange()?.format ?? 'musicxml');
  protected readonly dialogTitle = computed(() =>
    this.direction() === 'export'
      ? this.format() === 'musicxml'
        ? 'Export Score as MusicXML'
        : `Export Project as ${FormatNames[this.format()]}`
      : `Import ${FormatNames[this.format()]}`
  );
  protected readonly midiTracks = computed(() =>
    this.project.trackList().filter(t => t.kind === 'instrument' || t.kind === 'midi')
  );
  protected readonly audioTracks = computed(() => this.project.trackList().filter(t => t.kind === 'audio'));
  protected readonly sets = computed(() => this.project.score().sets);
  protected readonly selectedRegion = computed(() => {
    const midi = this.project.midiRegions();
    const id = [...this.selection.regions()].find(r => midi.has(r));
    return id ? (midi.get(id) ?? null) : null;
  });

  protected readonly form = signal<ExportForm>(this.defaults());
  protected readonly importForm = signal<ImportForm>(this.importDefaults());
  protected readonly touched = signal(false);
  protected readonly fileName = computed(() => exportFileName(this.format(), this.form()));
  protected readonly problems = computed(() => exportProblems(this.format(), this.form()));
  protected readonly sourceKey = computed(() => {
    const source = this.form().source;
    return source.kind === 'all' ? 'all' : source.kind === 'set' ? `set:${source.setId}` : `part:${source.trackId}`;
  });
  protected readonly accept = computed(() => ImportAccept[this.format()]);
  protected readonly pickFile = computed(() =>
    this.format() === 'midi' ? !!this.desktop?.dialogs?.importMidi : !!this.desktop?.dialogs?.importFile
  );
  protected readonly importName = computed(() => {
    const form = this.importForm();
    return form.file?.name ?? (form.path ? (form.path.split(/[\\/]/).pop() ?? form.path) : 'No file chosen');
  });
  protected readonly ready = computed(() =>
    this.direction() === 'export' ? true : !!(this.importForm().path || this.importForm().file)
  );

  private readonly jobId = signal<string | null>(null);
  protected readonly job = computed(() => {
    const id = this.jobId();
    return id ? (this.jobs.jobs().get(id) ?? null) : null;
  });
  protected readonly running = computed(() => isActive(this.job()));
  protected readonly percent = computed(() => Math.round((this.job()?.progress ?? 0) * 100));

  constructor() {
    // Each time it opens, it starts from the project as it is now.
    effect(() => {
      if (this.ui.interchange()) {
        untracked(() => {
          if (!this.running()) {
            this.form.set(this.defaults());
            this.importForm.set(this.importDefaults());
            this.jobId.set(null);
            this.touched.set(false);
          }
        });
      }
    });
    effect(() => {
      if (this.job()?.state === 'done') {
        untracked(() => this.ui.interchange.set(null));
      }
    });
  }

  private defaults(): ExportForm {
    const format = this.ui.interchange()?.format ?? 'musicxml';
    return {
      fileName: this.project.info()?.name ?? 'Untitled',
      directory: '',
      source: { kind: 'all' },
      compressed: false,
      embedMedia: true,
      trackIds: (format === 'aaf' ? this.audioTracks() : this.midiTracks()).map(t => t.id),
      format: 1,
      regionId: null
    };
  }

  private importDefaults(): ImportForm {
    return { path: '', file: null, importTempo: true, importMarkers: true, merge: false };
  }

  protected stateText(job: Job): string {
    switch (job.state) {
      case 'queued':
        return 'Waiting…';
      case 'running':
        return `${this.percent()} %`;
      case 'done':
        return 'Done';
      case 'failed':
        return 'Failed';
      default:
        return 'Stopped';
    }
  }

  protected patch(change: Partial<ExportForm>): void {
    this.form.update(form => ({ ...form, ...change }));
  }

  protected patchImport(change: Partial<ImportForm>): void {
    this.importForm.update(form => ({ ...form, ...change }));
  }

  protected toggleImport(key: 'importTempo' | 'importMarkers' | 'merge'): void {
    this.importForm.update(form => ({ ...form, [key]: !form[key] }));
  }

  protected setSource(key: string): void {
    const [kind, id = ''] = key.split(':');
    const source: ScoreSource =
      kind === 'set' ? { kind: 'set', setId: id } : kind === 'part' ? { kind: 'part', trackId: id } : { kind: 'all' };
    this.patch({ source });
  }

  protected toggleTrack(id: string): void {
    const ids = this.form().trackIds;
    this.patch({ trackIds: ids.includes(id) ? ids.filter(t => t !== id) : [...ids, id] });
  }

  protected async pickFolder(): Promise<void> {
    const folder = await this.desktop?.dialogs?.chooseFolder();
    if (folder) {
      this.patch({ directory: folder });
    }
  }

  protected async pick(): Promise<void> {
    const dialogs = this.desktop?.dialogs;
    const path =
      this.format() === 'midi'
        ? await dialogs?.importMidi?.()
        : await dialogs?.importFile?.(this.format() === 'aaf' ? 'aaf' : 'musicxml');
    if (path) {
      this.patchImport({ path, file: null });
    }
  }

  protected upload(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.patchImport({ file, path: '' });
  }

  protected close(): void {
    if (!this.running()) {
      this.ui.interchange.set(null);
    }
  }

  protected async cancelJob(): Promise<void> {
    const id = this.jobId();
    if (id) {
      await this.jobs.cancel(id);
    }
  }

  protected async start(): Promise<void> {
    this.touched.set(true);
    const format = this.format();
    let job: Job | null;
    if (this.direction() === 'export') {
      if (this.problems().length) {
        return;
      }
      const form = this.form();
      const run =
        format === 'musicxml'
          ? () => this.api.exportMusicXml(musicXmlRequest(form))
          : format === 'aaf'
            ? () =>
                this.api.exportAaf(
                  aafRequest(
                    form,
                    this.audioTracks().map(t => t.id)
                  )
                )
            : () =>
                this.api.exportMidi(
                  midiRequest(
                    form,
                    this.midiTracks().map(t => t.id)
                  )
                );
      job = await this.jobs.interchange(run, `Could not export the ${FormatNames[format]}`);
    } else {
      const form = this.importForm();
      const file = form.file;
      const run = file
        ? format === 'musicxml'
          ? () => this.api.uploadMusicXml(file)
          : format === 'aaf'
            ? () => this.api.uploadAaf(file)
            : () =>
                this.api.uploadMidi(file, {
                  importTempo: form.importTempo,
                  importMarkers: form.importMarkers,
                  merge: form.merge
                })
        : format === 'musicxml'
          ? () => this.api.importMusicXml({ path: form.path })
          : format === 'aaf'
            ? () => this.api.importAaf({ path: form.path })
            : () => this.api.importMidi(midiImportRequest(form));
      job = await this.jobs.interchange(run, `Could not import the ${FormatNames[format]}`);
    }
    if (job) {
      this.jobId.set(job.id);
    }
  }
}
